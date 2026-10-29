# Development

## Architecture

The project consists of three layers with different levels of abstraction.

- InstantReplay (high-level C# API that depends on Unity)
  - Asynchronous encoder pipeline
  - Ring buffer for compressed data
  - Session management
  - Implementations of video and audio providers
- UniEnc (low-level C# API that does not depend on Unity)
  - Also published on NuGet
  - Interfaces for encoders and muxers
- unienc_c (native library written in Rust)
  - Per-platform abstraction of encoders and muxers
  - Functions exposed through a C FFI
  - Details: `InstantReplay.Externals/unienc/README.md`

## Prerequisites

- Unity 2022.3 or later. The exact editor version of the development project is recorded in `ProjectSettings/ProjectVersion.txt`.
- .NET SDK 8 or later, to build and run the projects in `InstantReplay.Externals.sln`.
- The toolchains for the native library are only required when building it locally. See `InstantReplay.Externals/unienc/README.md`.

### Cloning the Repository

The Rust workspace depends on git submodules. Clone the repository with the submodules, or initialize them after cloning.

```sh
git clone --recursive <repository URL>
# or, in an existing clone
git submodule update --init --recursive
```

`Assets/Samples` is a symbolic link. On Windows, enable symbolic links for git (`git config core.symlinks true`, which requires Developer Mode or administrator privileges) before cloning; otherwise the link is checked out as a plain text file.

The prebuilt native binaries are committed to the repository, so the Unity project and the .NET projects can be opened without building the native library.

## Project Structure

- InstantReplay: Root of the Unity project
  - Assets
    - Samples: Symbolic link to `Packages/jp.co.cyberagent.instant-replay/Samples~`
  - Packages
    - jp.co.cyberagent.instant-replay: Main package
      - Runtime
        - InstantReplay.asmdef
      - UniEnc
        - Plugins: Native libraries for Unity
        - Runtime
          - UniEnc.asmdef
      - UniEnc.Unity
        - Runtime
          - UniEnc.Unity.asmdef: Unity-dependent helpers for UniEnc
        - Editor
          - UniEnc.Unity.Editor.asmdef
      - Suite: Recorder interface suite (see `docs/recorder-interface-suite.md`)
        - Runtime
          - InstantReplay.Suite.asmdef
        - UI
          - InstantReplay.Suite.UI.asmdef
        - Sharing
          - Slack
            - InstantReplay.Suite.Sharing.Slack.asmdef
          - Discord
            - InstantReplay.Suite.Sharing.Discord.asmdef
      - Resources: Runtime assets such as shaders
      - Samples~
      - UniversalRP: Video provider for the Universal Render Pipeline
      - Cri: Audio provider for CRIWARE
      - Wwise: Audio provider for Wwise
    - jp.co.cyberagent.instant-replay.dependencies: Auxiliary package used to install the dependencies through UnityNuGet
  - InstantReplay.Externals: Directory for source files outside of Unity
    - src: Sources for pure .NET
      - InstantReplay.DiskBuffer.Tests: End-to-end tests of the disk-buffered mode
      - UniEnc: C# project used to publish the NuGet package
        - Plugins: Native libraries for the NuGet package (Unity-dependent features are omitted)
      - UniEnc.Example: End-to-end sample of UniEnc
    - InstantReplay.Externals.sln: Solution for pure .NET
    - unienc: Rust workspace
  - docs: Design documents
  - THIRD-PARTY-NOTICES.md: License notices of the dependencies used by the unienc native library
  - README.md
  - README.ja.md
  - CHANGELOG.md
  - DEVELOPMENT.md: This document

### EXCLUDE_INSTANTREPLAY

`EXCLUDE_INSTANTREPLAY` is a scripting define symbol that excludes the contents of the Instant Replay package from a build without removing the package from the dependencies. When the symbol is defined, the contents are excluded by the following mechanisms.

| Kind of content | Mechanism |
| --- | --- |
| Assemblies | Every asmdef in the package has `!EXCLUDE_INSTANTREPLAY` in `defineConstraints`. |
| Native plugins | `UniEnc.Unity/Editor/PluginsExcluder.cs` disables every plugin under `UniEnc/Plugins/` for the target platform before the build and restores the setting after the build. |
| Shaders loaded through Resources | `UniEnc.Unity/Editor/ShaderStripper.cs` strips all variants of the shaders listed in it. |
| Assets generated at build time | Build hooks such as `UniEnc.Unity/Editor/NativeLogLevelBaker.cs` are compiled only when the symbol is not defined. |

When adding a new asset, confirm that it is covered by one of these mechanisms:

- A new asmdef must have `!EXCLUDE_INSTANTREPLAY` in `defineConstraints`.
- A new native plugin must be placed under `UniEnc/Plugins/`, or `PluginsExcluder` must be extended to cover it.
- A new shader or other asset in a `Resources` directory is included in the build regardless of assemblies. A shader must be added to `ShaderStripper`; any other kind of asset needs its own exclusion.
- A new build hook that generates assets must be enclosed in `#if !EXCLUDE_INSTANTREPLAY`.

## Release

### Procedure

1. Create a branch for the release from the latest `main`, for example `feature/release_v1.2.3`.
2. Update the `version` field of both `Packages/jp.co.cyberagent.instant-replay/package.json` and `Packages/jp.co.cyberagent.instant-replay.dependencies/package.json`.
3. Add a section for the new version to `CHANGELOG.md`. The changelog is written for the users of the package, so describe how each change affects them rather than how it is implemented.
4. If the unienc sources have changed since the last build of the native binaries, run the `build-unienc.yml` workflow on the release branch. See `InstantReplay.Externals/unienc/README.md` for details.
5. Open a pull request. `check-native-binaries.yml` runs on the pull request because `package.json` has changed, and fails if the native binaries are outdated.
6. Merge the pull request into `main`.
7. Create a GitHub release on `main` with a tag named `v<version>`, for example `v1.2.3`. Publishing the release triggers the workflows described below.

### UPM

The Instant Replay package is installed through UPM as a git dependency that references the `release` branch. When a new release is published, the `update-release-branch.yml` workflow automatically moves the `release` branch to the tag of the release.

### NuGet

The `publish-nuget.yml` workflow is triggered by a new release and publishes the NuGet package built from `InstantReplay.Externals/src/UniEnc/UniEnc.csproj`. The package version is taken from the tag name with the leading `v` removed.

## Testing

### Native Library

CI runs only the tests of the native library; the Unity and C# sides are not tested in CI. The tests run on iOS (simulator), Android (emulator), Windows, macOS, Linux and WebAssembly (headless Chrome). See `InstantReplay.Externals/unienc/README.md` for running them locally.

### Disk Buffer

`InstantReplay.Externals/src/InstantReplay.DiskBuffer.Tests` compiles the storage layer of the disk-buffered mode and the in-memory buffer without UnityEngine and exercises them together with the real native encoder. It is a console application that returns a non-zero exit code when a check fails.

```sh
cd InstantReplay.Externals
dotnet run --project src/InstantReplay.DiskBuffer.Tests
```

The native library is loaded from `src/UniEnc/Plugins`, so the test runs only on the platforms for which a NuGet variant of the native library is present there (Windows x64, macOS and Linux x64).
