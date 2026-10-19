# Release readiness

The managed SDK source has an MIT license. No NuGet packages have been published; native runtime redistribution review remains incomplete. The MIT license applies only to original project-owned SDK material; each third-party component retains its own license, notice, and distribution conditions.

## Verified scope

Windows x64 with .NET 10 is the validated environment. Local CPU and CUDA recording and streaming inference runs completed. This evidence does not establish broad GPU or driver compatibility, clean-machine installation, Linux support, multi-GPU behavior, or CUDA kernel execution attribution. These remain unverified.

The managed SDK package contains no native DLLs and requires an application to provision a compatible runtime and model. CPU and CUDA runtime packages target Windows x64. Runtime assets are copied by the package build targets without filtering unsupported consumer platforms. Model weights remain external. `nvcuda.dll` is provided by the installed NVIDIA driver and is not packaged.

## Package status

| Package | Contents | Status |
|---|---|---|
| `NemotronDiarizationSharp` | Managed `net10.0` SDK, README, distribution status, and MIT license. No native files. | MIT applies to original project-owned SDK source. No managed SDK package has been published. |
| `NemotronDiarizationSharp.Runtime.Cpu` | Five Windows x64 engine DLLs and applicable notices. | Not published; complete binary provenance, dependency and notice coverage, and redistribution review remain incomplete. An external Microsoft Visual C++ x64 runtime, including OpenMP, is required. |
| `NemotronDiarizationSharp.Runtime.Cuda` | Thirteen Windows x64 engine, CUDA/cuBLAS, and Microsoft runtime DLLs with applicable notices. | Not published; emitted-component provenance and review of NVIDIA and Microsoft redistribution conditions remain incomplete. |
| `NemotronDiarizationSharp.AllRuntimes` | Dependency-only package referencing the SDK and both native runtime packages. | Not published; distribution requires satisfying both CPU and CUDA package conditions. |

Do not treat a successful package build or consumer restore as legal clearance. The runtime build targets copy DLLs but do not copy their license materials into application output. Any separately authorized runtime distribution must include applicable notices and satisfy the underlying terms. Third-party components and native binaries are not relicensed under the SDK's MIT license.

## Evidence

- [Distribution status](../distribution/DISTRIBUTION_STATUS.md) summarizes package scope and outstanding redistribution requirements.
- [Native inventory](../distribution/native-components.json) summarizes historically observed native component identities, hashes and metadata; it is not a current package manifest.
- [Native provenance](../distribution/native-provenance.md) distinguishes CPU archive matching from local CUDA build evidence and records unresolved composition limits.
- [Upstream license evidence](../distribution/upstream-licenses.md) identifies pinned source references and notice coverage.
- [Vendor redistribution evidence](../distribution/vendor-redistribution.md) records the NVIDIA and Microsoft conditions requiring review.

Clean-machine, cross-platform, and broad GPU compatibility claims require separate technical evidence even after redistribution rights are established.
