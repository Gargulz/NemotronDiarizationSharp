# Distribution status

No NuGet packages have been published. Native runtime redistribution review remains incomplete; this repository does not grant permission to redistribute third-party binaries. The original project-owned SDK source is licensed under MIT; that license does not cover NVIDIA, Microsoft, NeMo-Speech.cpp, model, or other third-party materials.

| Package | Current scope | Distribution status |
|---|---|---|
| `NemotronDiarizationSharp` | Managed .NET 10 SDK; contains no native DLLs and does not depend on a runtime package. Applications must provide a compatible native runtime and model separately. | Source and package metadata use MIT for original project-owned SDK code. No managed SDK package has been published. |
| `NemotronDiarizationSharp.Runtime.Cpu` | Five Windows x64 engine DLLs with upstream and supplemental notices. Consumers need a compatible Microsoft Visual C++ x64 runtime, including OpenMP. | Not published; complete binary provenance, dependency and notice coverage, and applicable redistribution review remain incomplete. |
| `NemotronDiarizationSharp.Runtime.Cuda` | Thirteen Windows x64 DLLs, including NVIDIA CUDA/cuBLAS and Microsoft runtime components. | Not published; emitted-component provenance and review of NVIDIA and Microsoft redistribution conditions remain incomplete. |
| `NemotronDiarizationSharp.AllRuntimes` | Dependency-only package that references the SDK, CPU, and CUDA packages. | Not published; distribution requires satisfying both native runtime packages' conditions. |

The managed SDK package can be considered independently, but inference still requires a separately obtained compatible runtime and model. An SDK-plus-CPU distribution avoids bundling NVIDIA CUDA libraries but still requires complete CPU binary provenance, dependency and notice coverage, and compliance with applicable redistribution conditions. SDK-plus-CUDA also requires review of NVIDIA application-distribution terms and Microsoft runtime eligibility. AllRuntimes inherits both runtime packages' distribution conditions. None of these packages has been published.

Windows x64 with .NET 10 is the validated environment. CPU and CUDA recording and streaming inference have completed in local validation. This does not establish general GPU or driver compatibility, clean-machine installation, Linux support, multi-GPU behavior, or CUDA kernel execution attribution. Those areas remain unverified. Runtime packages target Windows x64 and do not filter assets for unsupported consumer platforms.

Model weights are external and remain subject to their own terms. The CUDA driver library `nvcuda.dll` is supplied by the installed driver and is not packaged. Do not copy operating-system DLLs into runtime packages. Required upstream `LICENSE`, `NOTICE`, `THIRD_PARTY_NOTICES.md`, component notices, and the CUDA terms must remain with any applicable distribution. Successful restore, build, pack, or publish does not establish permission to redistribute these files.

The runtime build targets copy native DLLs, not their license materials, to application output. Distributors of any authorized runtime package must carry the applicable `licenses/` files as required by those terms. The relative links inside the upstream notice documents refer to paths in their complete source trees; this repository contains only a scoped source snapshot. The relevant local license and notice texts are preserved alongside the package, and the partial-snapshot boundary is recorded in [upstream license evidence](upstream-licenses.md).

See [native inventory](native-components.json), [native provenance](native-provenance.md), [upstream license evidence](upstream-licenses.md), and [vendor redistribution evidence](vendor-redistribution.md) for component identities, hashes, notices, and unresolved conditions. This evidence does not grant redistribution rights.
