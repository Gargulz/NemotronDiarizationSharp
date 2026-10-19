# Native artifact provenance

This distribution evidence record is dated 2026-10-18. It does not grant a license, authenticate an upstream build, establish complete binary composition, or authorize publication. `OBSERVED` describes files and records inspected locally; `INFERRED` describes their implications; `UNKNOWN` identifies unresolved evidence or release blockers.

## Inventory evidence

[`native-components.json`](native-components.json) summarizes 18 historically observed native assets (five CPU and thirteen CUDA): recorded component paths, sizes, SHA-256 values, imports, source-identity results, applicable notices and limitations. It is a derived component summary, not a current package manifest. The complete original package audit, including immutable archive identities and original nuspecs, is retained privately; its SHA-256 is recorded in the summary.

The public summary can be compared with separately obtained native component files. It intentionally omits historical package archive identities, original nuspecs and local build-directory paths. Recorded hashes and metadata do not execute or authenticate binaries, establish complete static or dynamic composition, or grant redistribution rights.

PE import and delay-import tables are parsed from hash-matched staged files without loading them. Import tables establish a minimum direct dependency set. Imported `LoadLibrary*`/`GetProcAddress` functions are recorded as capabilities, not proof of a particular dynamically loaded file. Internal static components, optional loading paths, forwarded exports, operating-system resolution and device execution cannot be determined completely this way. Version resources and company names are descriptive, unauthenticated file metadata.

## CPU package

**OBSERVED:** the selected package DLLs match both their staged files and entries in the preserved archive `nemo-speech-0.2.0-windows-x86_64-cpu.zip`. Archive SHA-256 is `82C80451086E86194ABA9AF19F800DA77CD66D1C28D993D0EFCEFFD6CDB49BB8`. The archive is recorded as the [official v0.2.0 release artifact](https://github.com/NVIDIA/NeMo-Speech.cpp/releases/tag/v0.2.0). This audit verifies its local bytes against the existing pinned manifest; it does not redownload the artifact or authenticate its producer.

| Package asset | Project | Binary version resource | Provenance limit |
|---|---|---|---|
| `ggml-base.dll` | ggml / NeMo integration | Absent | Release ZIP entry match |
| `ggml-cpu.dll` | ggml / NeMo integration | Absent | Release ZIP entry match |
| `ggml.dll` | ggml / NeMo integration | Absent | Release ZIP entry match |
| `nemo_speech_asr.dll` | NeMo-Speech.cpp | Absent | Release ZIP entry match |
| `nemo_speech_asr_c.dll` | NeMo-Speech.cpp | Absent | Release ZIP entry match |

All five live under `runtimes/nemotron/cpu/win-x64/native/`. Their exact hashes and static imports are in the JSON. The archived 18-DLL set is broader than the selected five-DLL package; the [`cpu-runtime-manifest.json`](../../src/NemotronDiarizationSharp.Runtime.Cpu/cpu-runtime-manifest.json) and [`RUNTIME_COMPONENTS.md`](../../src/NemotronDiarizationSharp.Runtime.Cpu/licenses/RUNTIME_COMPONENTS.md) explain the archive contents. The upstream license and notice texts are preserved; supplemental notices are recorded separately without changing their source text.

**UNKNOWN:** the release archive's compiler/linker inputs, actual CMake configuration, authenticated build receipt, exact linked SentencePiece/Abseil/protobuf/Darts revisions, full static dependency composition and KenLM inclusion. The pinned [NeMo source commit](https://github.com/NVIDIA/NeMo-Speech.cpp/tree/6a3ca369370782acee0dd155e34394ba60b920c4) and [llama.cpp gitlink](https://github.com/ggml-org/llama.cpp/tree/bd4f514db14d87fded667787a7a963bfbaa98e89) establish source references, not proof that those exact trees produced this release ZIP. Existing ASR diagnostics mention SentencePiece and Darts-clone; absence of separate DLLs does not prove absence of static linkage. A default-disabled Flashlight option or disabled-decoder diagnostic does not establish complete LGPL exclusion for this upstream binary.

The CPU package omits Microsoft runtime DLLs. PE imports identify `MSVCP140.dll`, `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll`, and `VCOMP140.DLL` as external prerequisites, in addition to Windows/UCRT components. The archived runtime version `14.44.35211.0` and PE linker version `14.44` do not establish an exact minimum supported redistributable. Consumer installation of an appropriate supported x64 Visual C++ runtime is the existing prerequisite strategy; no installer is bundled here.

## CUDA package

**OBSERVED:** thirteen packaged DLLs match the CUDA staging manifest and, where still available, their recorded source files. This is **local build/installation provenance**, not a match to an upstream CUDA release archive. The component summary preserves the recorded source-file identity result without publishing the original local source-directory paths.

| Package asset(s) | Recorded source | Observed version |
|---|---|---|
| `nemo_speech_asr_c.dll`, `nemo_speech_asr.dll` | Local CUDA build output | No version resource; manifest identifies native API version `nemo-speech-asr 0.2.0` |
| `ggml.dll`, `ggml-base.dll`, `ggml-cpu.dll`, `ggml-cuda.dll` | Same local build `bin` | No version resource |
| `cudart64_12.dll` | CUDA `v12.9/bin` | Resource `6,14,11,12090`; installation manifest identifies cudart `12.9.79` |
| `cublas64_12.dll`, `cublasLt64_12.dll` | CUDA `v12.9/bin` | Resource `6,14,11,1291`; installation manifest identifies cuBLAS `12.9.1.4` |
| `msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll` | VS 2026 Community `VC/Redist/MSVC/14.51.36231/x64/Microsoft.VC145.CRT` | Actual file resource `14.51.36247.0` |
| `vcomp140.dll` | VS 2022 Community `VC/Redist/MSVC/14.44.35112/x64/Microsoft.VC143.OpenMP` | Actual file resource `14.44.35211.0` |

All thirteen live under `runtimes/nemotron/cuda/win-x64/native/`. Directory names are not substituted for observed DLL versions. The CUDA installation's `version.json` identifies SDK `12.9.1`, cudart `12.9.79`, cuBLAS `12.9.1.4`, and nvcc `12.9.86`. Its original hash is retained in the privately preserved complete audit; the component versions described here do not authenticate an installation or grant redistribution rights.

### Recovered local build evidence

The recorded source-build directory still contains `CMakeCache.txt`, `build.ninja`, `compile_commands.json`, `.ninja_log`, compiler-detection records, a vcpkg install log and a materialized llama.cpp tree. The complete original audit retains their hashes and build-input records privately; the selected configuration and library names are summarized below.

**OBSERVED configuration:** Release; Ninja; MSVC tool directory `14.44.35207` (detected compiler `19.44.35224.0`); nvcc `12.9.86`; CUDA architecture `89`; shared ggml; CPU/CUDA/OpenMP enabled; dynamic backend discovery disabled; diarization enabled; ASR executable disabled; Flashlight disabled; upstream NeMo ggml patches enabled. The source checkout's `.git/HEAD` names pinned NeMo commit `6a3ca369370782acee0dd155e34394ba60b920c4`; the llama.cpp submodule HEAD names `bd4f514db14d87fded667787a7a963bfbaa98e89`. `patches/series` and the materialization stamp are fingerprinted. These observed HEAD files do not prove that the source worktree was unmodified when compiled.

**OBSERVED link inputs:** the ASR implementation target lists `sentencepiece.lib`, `libprotobuf.lib`, numerous `absl_*.lib` libraries and `utf8_validity.lib` under vcpkg triplet `x64-windows-static-md`, alongside the NeMo and ggml libraries. The install log lists SentencePiece `0.2.1`, Abseil `20260107.1#3`, protobuf `6.33.4#2` and utf8-range `6.33.4`. Its detected vcpkg dependency compiler is from VS 2026; do not attribute all linked code to the top-level VS 2022 compiler. CUDA target link inputs also name `cudadevrt.lib` and `cudart_static.lib` as well as import libraries for CUDA/cuBLAS.

**INFERRED:** static SentencePiece/protobuf/Abseil/utf8-range code and bundled SentencePiece subcomponents may be relevant to the emitted ASR DLL, so preserve their verified notices. The configuration/link graph supplies stronger evidence for disabled Flashlight/KenLM in this local CUDA build than is available for the upstream CPU archive. It does not provide an independently authenticated emitted-object inventory or relink/reproducibility receipt. Static-library link inputs do not prove that every library member or every subcomponent survives linker elimination. Keep binary-specific KenLM exclusion and exhaustive composition **UNKNOWN**, rather than treating configuration as legal clearance.

### Native dependency closure

**OBSERVED:** `ggml-cuda.dll` statically imports `cudart64_12.dll`, `cublas64_12.dll` and **`nvcuda.dll`**. `cublas64_12.dll` statically imports `cublasLt64_12.dll`. `nvcuda.dll` remains an external NVIDIA driver component; it is not packaged. These are ordinary PE imports, not inferred dynamic dependencies. A focused Microsoft `dumpbin /dependents` check corroborated these relationships independently of the audit parser.

Windows/UCRT dependencies remain external. The bundled Microsoft CRT/OpenMP files contain dynamic-loader calls; absence of another CRT filename in direct imports does not establish complete closure. Full vendor-internal dynamic loading and clean-machine dependencies remain **UNKNOWN**. This record does not claim additional native execution or clean-machine installation.

## Distribution implications

1. **Hash identity:** the component summary retains observed native hashes and stage/archive/source identity results; original package ZIP-entry receipts remain in the privately preserved complete audit.
2. **Source reference:** pinned source IDs and recovered local configuration narrow the investigation, but are not equivalent to a reproducible or authenticated build receipt.
3. **License applicability:** upstream source licenses, vendor binary redistribution terms and the actual emitted binary composition must be assessed separately. See [`upstream-licenses.md`](upstream-licenses.md), [`vendor-redistribution.md`](vendor-redistribution.md), and the retained upstream [`THIRD_PARTY_NOTICES.md`](../../src/NemotronDiarizationSharp.Runtime.Cpu/licenses/THIRD_PARTY_NOTICES.md).
4. **Outstanding evidence:** CPU binary-specific static composition remains particularly weak. CUDA has more local build evidence but still requires full third-party applicability and NVIDIA/Microsoft redistribution conditions to be satisfied.
5. **Alternative deployment:** consumer-installed NVIDIA driver and CPU Visual C++ prerequisites are already part of the deployment model. Separately installed CUDA/cuBLAS or a central VC redistributable could reduce bundled vendor-DLL obligations, but would require an explicit, technically validated packaging decision. This repository does not remove DLLs or change the loader to presume that model.

Managed-only packaging contains no native DLLs and can be assessed independently of native-binary redistribution; the original project-owned SDK source is MIT-licensed. It still needs a separately obtained compatible native runtime to perform diarization. CPU, CUDA and AllRuntimes packages have not been published; component-specific build-composition evidence and applicable third-party redistribution review remain incomplete.
