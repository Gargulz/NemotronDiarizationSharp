# Release readiness

The managed SDK `NemotronDiarizationSharp` version `0.1.0-preview.1` has been published. The next managed update is planned as `0.1.0-preview.2`; its manual release workflow remains gated and publication requires separate authorization. Native runtime packages remain unpublished and are not cleared for redistribution. The MIT license applies only to original project-owned SDK material; each third-party component retains its own license, notice, and distribution conditions.

## Verified scope

Windows x64 with .NET 10 is the validated environment. Local CPU and CUDA recording and streaming inference runs completed. This evidence does not establish broad GPU or driver compatibility, clean-machine installation, Linux support, multi-GPU behavior, or CUDA kernel execution attribution. These remain unverified.

The managed SDK package contains no native DLLs and requires an application to provision a compatible runtime and model. CPU and CUDA runtime packages target Windows x64. Runtime assets are copied by the package build targets without filtering unsupported consumer platforms. Model weights remain external. `nvcuda.dll` is provided by the installed NVIDIA driver and is not packaged.

## Package status

| Package | Contents | Status |
|---|---|---|
| `NemotronDiarizationSharp` | Managed `net10.0` SDK, NuGet README, distribution status, and MIT license. No native files or model. | `0.1.0-preview.1` published. `0.1.0-preview.2` is the next prepared version; not authorized for publication by this task. |
| `NemotronDiarizationSharp.Runtime.Cpu` | Local development project for five Windows x64 engine DLLs and applicable notices. | Not part of the managed prerelease and not available from NuGet. Complete binary provenance, dependency and notice coverage, and redistribution review remain incomplete. An external Microsoft Visual C++ x64 runtime, including OpenMP, is required. |
| `NemotronDiarizationSharp.Runtime.Cuda` | Local development project for thirteen Windows x64 engine, CUDA/cuBLAS, and Microsoft runtime DLLs with applicable notices. | Not part of the managed prerelease and not available from NuGet. Emitted-component provenance and review of NVIDIA and Microsoft redistribution conditions remain incomplete. |
| `NemotronDiarizationSharp.AllRuntimes` | Local dependency-only project for the SDK and both native runtime package IDs. | Not part of the managed prerelease and not available from NuGet. Distribution requires satisfying both CPU and CUDA package conditions. |

The managed package metadata points to the public GitHub repository. Its companion `.snupkg` carries portable debug symbols with Source Link data. The `.nupkg` contains no dependency on the local CPU, CUDA, or AllRuntimes projects. The presence of package projects in this repository does not make them installable dependencies.

Do not treat a successful package build or consumer restore as legal clearance. The local runtime project targets copy DLLs but do not copy their license materials into application output. Any separately authorized runtime distribution must include applicable notices and satisfy the underlying terms. Third-party components and native binaries are not relicensed under the SDK's MIT license.

## Evidence

- [Distribution status](../distribution/DISTRIBUTION_STATUS.md) summarizes package scope and outstanding redistribution requirements.
- [Native inventory](../distribution/native-components.json) summarizes historically observed native component identities, hashes and metadata; it is not a current package manifest.
- [Native provenance](../distribution/native-provenance.md) distinguishes CPU archive matching from local CUDA build evidence and records unresolved composition limits.
- [Upstream license evidence](../distribution/upstream-licenses.md) identifies pinned source references and notice coverage.
- [Vendor redistribution evidence](../distribution/vendor-redistribution.md) records the NVIDIA and Microsoft conditions requiring review.

Clean-machine, cross-platform, and broad GPU compatibility claims require separate technical evidence even after redistribution rights are established.

## Before a future managed update

1. Review and accept the exact locally validated source commit and its release notes.
2. Confirm the `.nupkg` is `NemotronDiarizationSharp.0.1.0-preview.2.nupkg`, contains only the managed SDK and intended documentation/license files, and has the expected repository, license, version, and Source Link metadata.
3. Publish only that managed package after separate HUMAN authorization. Do not publish CPU, CUDA, or AllRuntimes packages until their provenance and redistribution gates are cleared.
4. Verify the published package can be restored into a clean .NET 10 project, then update package availability wording if the release changes.
