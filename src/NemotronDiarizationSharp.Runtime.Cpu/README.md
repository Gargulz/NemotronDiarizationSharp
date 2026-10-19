# Windows x64 CPU runtime evidence

The native runtime package has not been published; redistribution review remains incomplete. See the [distribution status](../../docs/distribution/DISTRIBUTION_STATUS.md)
and [native provenance](../../docs/distribution/native-provenance.md). The
package includes the original archive manifest under `provenance/`; that
manifest describes all 18 original DLLs, while this package selects only the
five engine DLLs below. Supplemental notices are included conservatively.

This local development project contains the original 18-DLL Windows x64 CPU
inventory from NeMo-Speech.cpp v0.2.0 and the release's 11 license/notice files.
The managed project targets .NET 10. Native binaries remain local inputs;
redistribution review remains incomplete and no native NuGet package has been published.
The NuGet license metadata is intentionally unset (**NEEDS_REVIEW**): these
component notices do not establish one license grant for the complete package.

## Release identity

[cpu-runtime-manifest.json](cpu-runtime-manifest.json) records schema version 1,
the complete original DLL and license inventory, sizes, SHA-256 values,
observed versions/imports, dependency classifications and evidence limitations.

- Archive: `nemo-speech-0.2.0-windows-x86_64-cpu.zip`, 5,528,418 bytes.
- SHA-256: `82C80451086E86194ABA9AF19F800DA77CD66D1C28D993D0EFCEFFD6CDB49BB8`.
- DLL entry prefix: `nemo-speech-0.2.0-windows-x86_64-cpu/bin/`.
- Pinned source reference: `6a3ca369370782acee0dd155e34394ba60b920c4`.
- Pinned llama.cpp gitlink reference: `bd4f514db14d87fded667787a7a963bfbaa98e89`.

A local package audit verified the archive's full hash and compared all 18 DLLs and 11 license
files byte-for-byte with their ZIP entries. This establishes present content
identity. It does not prove staging history, the compiler configuration or
that the pinned source references produced those binaries. Build/toolchain
provenance and actual KenLM linkage remain **UNKNOWN**.

## Provisional diarization dependency candidate

The observed PE imports identify five engine DLLs:

```text
nemo_speech_asr_c.dll
nemo_speech_asr.dll
ggml.dll
ggml-base.dll
ggml-cpu.dll
```

Their static closure also imports `msvcp140.dll`, `vcruntime140.dll`,
`vcruntime140_1.dll` and `vcomp140.dll`, plus Windows system/UCRT libraries.
A separate local CPU smoke test completed one isolated eight-second inference with exactly these five
engine DLLs staged. It observed the four CRT modules in Windows `System32` at
version `14.51.36247.0`. This supports that one tested path; it does not
establish coverage for every model or code path or the exact minimum compatible
CRT version. ggml exposes backend loading and imports
`LoadLibraryW`/`GetProcAddress`; Microsoft runtime DLLs also have dynamic
loading imports.

`llama.dll`, the two NMT DLLs, TTS and five additional Microsoft runtime DLLs
are outside this observed static closure. They remain in the original
18-file inventory because static import absence does not establish removal
safety. The CPU runtime project now explicitly selects the five engine DLLs
for package metadata and build/publish output. The original DLL and license
files remain on disk unchanged.

The CPU runtime package metadata selects these five DLLs for
`runtimes/nemotron/cpu/win-x64/native`, and its `buildTransitive` target keeps
that subtree in app build and publish output. It does not select the other 13
files in the original inventory. The earlier ZIP audit confirmed the five DLLs and their
manifest hashes, plus 12 unique package paths under `licenses/`. The current package retains
those paths and adds Darts-clone and ggml file-level notices, for 14 license
paths. This audit
does not establish redistribution rights: license and build-provenance review
remains incomplete; this evidence does not grant permission to redistribute binaries.

The five-DLL CPU candidate relies on an externally provided Microsoft Visual C++
v14 Redistributable **x64 prerequisite**. An application deployment must ensure
the required CRT is available. The smoke test observed `MSVCP140.dll`,
`VCRUNTIME140.dll`, `VCRUNTIME140_1.dll` and `VCOMP140.dll` in Windows
`System32`, each at version `14.51.36247.0`. This is an observed runtime
version, not a determination of the exact minimum compatible version. These
DLLs are not included in the five-file candidate, and this project does not
install the Redistributable or change `PATH` or machine-wide DLL search
settings. See [Microsoft deployment guidance](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files)
and [version requirements](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist).

The five-file selection is a technical staging candidate. It does not establish
complete coverage for every model or code path, and it does not change the
outstanding redistribution requirements.

CPU and CUDA runtimes reuse native DLL names, so their package assets remain in
separate `runtimes/nemotron/cpu/win-x64/native` and
`runtimes/nemotron/cuda/win-x64/native` output subdirectories. The managed
loader selects and loads one backend by absolute entry-library path per
process; it does not load both backends into the same process. Standalone
consumer publish validation confirmed that both package subdirectories
coexist without overwriting one another.

## Runtime selection

The managed SDK package contains no native runtime DLLs. With the default
`DiarizationBackend.Auto`, the SDK uses CUDA when its Windows x64 pre-load
device check succeeds, then chooses this CPU runtime when CUDA is unavailable
and the CPU package is installed. Use `Backend = DiarizationBackend.Cpu` to
require CPU. `GpuDeviceIndex` does not select a backend; when CUDA is selected,
null means device 0. An invalid explicit runtime directory or a native load
failure is terminal, and the SDK does not switch backends after loading starts.
`NEMOTONDIARIZATIONSHARP_NATIVE_DIR` can point to an absolute custom runtime
directory; its entry library and dependencies must match the selected backend.

## Verification

Run these commands from the repository root using existing Windows PowerShell.
`-ExecutionPolicy Bypass` applies only to the child process; no persistent
execution policy is changed.

Metadata-only validation needs no ZIP, DLLs or license artifacts:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .github/scripts/Verify-CpuRuntimeManifest.ps1 -Mode Metadata
```

Full local artifact verification explicitly supplies the original archive and
staged directories:

```powershell
$archivePath = '<path-to-verified-cpu-archive.zip>'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .github/scripts/Verify-CpuRuntimeManifest.ps1 -Mode Artifacts -ArchivePath $archivePath -NativeDirectory src/NemotronDiarizationSharp.Runtime.Cpu/runtimes/win-x64/native -LicenseDirectory src/NemotronDiarizationSharp.Runtime.Cpu/licenses
```

`-ManifestPath` can select another manifest. Artifact mode can also check only
the explicitly supplied artifact sets; its output labels such checks partial.
Only all three paths produce a full artifact verification result. The script
opens files/ZIP entries for reading, compares sizes and SHA-256 values, and
never extracts, modifies, downloads or executes native files. It returns a
nonzero exit code on failure and names the failing entry. Metadata-only success
validates the recorded schema/inventory; it does not reverify provenance.

Schema 1 requires the fixed original 18 filenames and 11 release license paths.
`files` records PE observations and `ENGINE`, `OTHER_ENGINE` or
`MICROSOFT_RUNTIME` classifications. `licenseFiles` records original ZIP
materials only; the accompanying `RUNTIME_COMPONENTS.md` is repository
explanation. `evidence`, `dependencyAnalysis` and `externalRuntimePrerequisites`
preserve the unknowns, provisional candidate and outstanding redistribution requirements.
The verifier validates metadata and artifact hashes; it does not reproduce
PE analysis, determine legal obligations or validate runtime completeness.

## Redistribution requirements

[licenses/RUNTIME_COMPONENTS.md](licenses/RUNTIME_COMPONENTS.md) documents the
preserved materials and historical coverage gaps, including the formerly
missing Darts-clone notice and potential KenLM obligations. The package supplements
the package with the verified Darts-clone and ggml file-level notices without
rewriting the archive record. Binary applicability and KenLM obligations
remain unresolved. Archive matching is not legal clearance.
Changes to the native file set and binary redistribution require
dependency validation and compliance with applicable component terms; license review remains incomplete. This
inventory describes Windows x64 only; other platforms are unverified.
