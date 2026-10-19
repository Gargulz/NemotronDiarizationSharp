# NVIDIA CUDA and Microsoft runtime evidence

Observed on 2026-10-18. This is a technical evidence record for a qualified
reviewer, not a legal opinion or an assertion of distributor entitlement.
Exact DLL hashes, file-resource versions, import edges and staged-source identity results
are in [native-components.json](native-components.json).

## NVIDIA: exact components and authoritative terms

The staged `cudart64_12.dll`, `cublas64_12.dll`, `cublasLt64_12.dll` originate,
according to the existing CUDA manifest, in the installed CUDA `v12.9/bin`
directory. This evidence compares their bytes and hashes with those exact sources. The
installation's `version.json` reports **CUDA SDK 12.9.1**, cudart **12.9.79**,
cuBLAS **12.9.1.4**, nvcc **12.9.86**. File-resource versions are separate
fields; in particular a cuBLAS resource string is not its toolkit component
version. Matching local installation bytes is not a vendor build attestation.

Primary source: [archived CUDA 12.9.1 EULA](https://docs.nvidia.com/cuda/archive/12.9.1/eula/index.html),
particularly §§1.1.1–1.1.2, 1.1.6, 1.2, 1.7 and the CUDA supplement
§§2.1–2.2, Attachment A (§2.6), Attachment B (§2.7).
The installed toolkit `EULA.txt`, dated January 12, 2025, is preserved byte for
byte as [CUDA-12.9.1-EULA.txt](licenses/CUDA-12.9.1-EULA.txt), SHA-256
`72C22161FC1EBF242443D45158A7DD382D68597B00EEB7A7F539BE5C6114BE33`.
It was checked against the official archived terms for the relevant sections;
HTML and local text are different representations, not asserted identical
files. The installation also has a `LICENSE` file with different bytes; no
claim of equivalence is made merely from its name.

Attachment A lists CUDA Runtime (`cudart.dll`) and CUDA BLAS (`cublas.dll`,
`cublasLt.dll`), including version/architecture filename variations. Therefore
these three filenames fall within the documented distributable component
families. This is **VERIFIED / DOCUMENTED** for listing applicability, not
clearance for the current package.

The grant concerns incorporation into an application with additional
functionality and controlled access to the bundled portions; downstream terms
must remain consistent. Standalone SDK distribution is restricted. The CUDA
supplement limits development to NVIDIA GPU systems, and the agreement also
contains export/end-user restrictions. Required component notices must be
preserved. The sample-source notice applies if such sample derivatives are
actually distributed; it is not a universal copyright replacement.

**NEEDS LEGAL REVIEW:** does this reusable runtime NuGet package, consumed by
arbitrary third-party applications, satisfy the application/incorporation,
access and downstream-agreement conditions? Does the intended distributor and
destination satisfy the applicable grant? Do not treat an MIT/Apache SDK
license as relicensing the NVIDIA DLLs. No credentials or private eligibility
records were accessed. No license acceptance was inferred from installation.

`nvcuda.dll` is an external **static PE import** of `ggml-cuda.dll` in this
build; the Windows driver is not packaged. `cublasLt64_12.dll` is imported by
`cublas64_12.dll`. API import tables do not establish every possible runtime
load or CUDA kernel execution. Toolkit-wide Attachment B does not establish
that CUDA-GDB, Thrust or every other toolkit component is in these binaries.

## Microsoft: CRT and OpenMP

The CUDA manifest records these sources, whose versions must be taken from
the DLL resources rather than the enclosing Redist directory name:

| Packaged file | Manifest source family | Observed file version |
|---|---|---|
| msvcp140.dll | VS 2026 Community, VC145 CRT, Redist 14.51.36231 | 14.51.36247.0 |
| vcruntime140.dll | same | 14.51.36247.0 |
| vcruntime140_1.dll | same | 14.51.36247.0 |
| vcomp140.dll | VS 2022 Community, VC143 OpenMP, Redist 14.44.35112 | 14.44.35211.0 |

Primary sources, inspected in full where relevant:

- [VS 2026 redistribution list](https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution),
  Visual C++ Runtime Files: the release Redist tree is covered subject to
  licensing, with exclusions for debug/nonredistributable/preview components.
- [VS 2022 redistribution list](https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution),
  corresponding VC143 CRT/OpenMP folders and restrictions.
- [Redistribute Visual C++ files](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files):
  entitlement is limited to licensed Visual Studio users; the Microsoft
  redistributable installer is a documented prerequisite deployment option.
- [VS Community 2026 terms landing page](https://visualstudio.microsoft.com/license-terms/vs2026-ga-community/),
  linked [official DOCX](https://visualstudio.microsoft.com/wp-content/uploads/2025/10/Visual_Studio_2026-License-Community_ENU.docx),
  downloaded SHA-256 `02AC07637F4A513A807CD3721C7620433FC483BC049D25784B0C10C54B05E1FB`;
  document date October 1, 2025, Distributable Code section.
- [VS Community 2022 terms landing page](https://visualstudio.microsoft.com/license-terms/vs2022-ga-community/),
  linked [official DOCX](https://visualstudio.microsoft.com/wp-content/uploads/2021/11/Visual-Studio-2022-Community-License-EN.docx),
  downloaded SHA-256 `41A207B10C8AB91D0D2F10A854715F73DCA54509581692D2FE179AA3FFCB8540`;
  §4, Distributable Code. Raw documents and extracted text remain ignored
  ignored local research inputs.

The lists identify eligible files, subject to the actual Visual Studio terms.
Both agreements condition distribution on added functionality and protective
downstream terms; the 2026 document adds an indemnification provision and
restrictions for Insiders/Unreleased Components. The current installation's
path, resource metadata and a Redist-list match do not prove the owner's
entitlement or the originating toolchain's release channel.

**NEEDS LEGAL REVIEW / MISSING EVIDENCE:** establish the actual applicable
license and release-channel provenance, and whether SDK/runtime NuGet delivery
meets these conditions. In particular, obtain confirmation for the three
VC145 CRT files; do not automatically apply VS 2022 terms to VS 2026 files.
Do not label Microsoft DLLs MIT or Apache-2.0. Preserve their existing
copyright/version resources; a custom attribution does not supply a grant.

## Supported alternative to investigate, without changing deployment

CPU packaging already relies on a consumer-installed Microsoft Visual C++
v14 x64 Redistributable (including OpenMP); it does not bundle Microsoft DLLs.
That documented installer prerequisite avoids our direct redistribution of
those four files. The exact minimum compatible version is unknown without
build evidence; Prior local successes on this host are not clean-machine checks.

If current CUDA bundling cannot be cleared, evaluate consumer provisioning of
the official CUDA 12.9.1 runtime/cuBLAS and Microsoft Redistributable under
their own terms. The existing explicit native-directory option can consume a
complete compatible directory. The current loader searches its selected
directory and System32 for dependencies: merely installing CUDA into a toolkit
directory or adding PATH is not proof that this deployment works. A documented
provisioning mechanism and focused loading/clean-host checks would be needed.
No DLL was removed, no deployment target was changed, and this alternative
has not been implemented or validated.
