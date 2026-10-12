# CPU runtime component evidence

Native runtime redistribution review remains incomplete; this evidence does not grant permission to redistribute binaries. Evidence statuses in this document are
**VERIFIED** (directly checked), **NEEDS_REVIEW** (requires applicable license or
specialist assessment) and **UNKNOWN** (not established by available evidence).
Technical evidence audit success does not grant legal clearance.

## Preserved release materials — VERIFIED

The preserved archive is `nemo-speech-0.2.0-windows-x86_64-cpu.zip` (5,528,418 bytes),
SHA-256 `82C80451086E86194ABA9AF19F800DA77CD66D1C28D993D0EFCEFFD6CDB49BB8`.
All 11 original license/notice files match their ZIP entries byte-for-byte.
Their exact hashes and sizes are in
[../cpu-runtime-manifest.json](../cpu-runtime-manifest.json).

Each relative path below maps to the same suffix under archive prefix
`nemo-speech-0.2.0-windows-x86_64-cpu/share/licenses/nemo-speech/`.

| Included file | Observed material | Match status |
|---|---|---|
| [LICENSE](LICENSE) | NeMo-Speech.cpp Apache-2.0 license material | VERIFIED |
| [NOTICE](NOTICE) | NVIDIA attribution and notice reference | VERIFIED |
| [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) | Broad source-tree component notices | VERIFIED |
| [third_party/ggml/LICENSE](third_party/ggml/LICENSE) | MIT | VERIFIED |
| [third_party/llama.cpp/LICENSE](third_party/llama.cpp/LICENSE) | MIT | VERIFIED |
| [third_party/cpp-httplib/LICENSE](third_party/cpp-httplib/LICENSE) | MIT | VERIFIED |
| [third_party/miniaudio/LICENSE](third_party/miniaudio/LICENSE) | Public-domain/MIT-0 alternatives | VERIFIED |
| [third_party/vcpkg/abseil/LICENSE](third_party/vcpkg/abseil/LICENSE) | Apache-2.0 text | VERIFIED |
| [third_party/vcpkg/protobuf/LICENSE](third_party/vcpkg/protobuf/LICENSE) | BSD license material | VERIFIED |
| [third_party/vcpkg/sentencepiece/LICENSE](third_party/vcpkg/sentencepiece/LICENSE) | Apache-2.0 text | VERIFIED |
| [third_party/vcpkg/utf8-range/LICENSE](third_party/vcpkg/utf8-range/LICENSE) | MIT | VERIFIED |

No original archive license file was lost during staging. This document is
additional repository explanation, not a twelfth original release license.
The original texts and broad notices remain unchanged. Some upstream-relative
links in THIRD_PARTY_NOTICES use the source-tree layout rather than this
installed layout; a broken link does not by itself mean its text is missing.

## Component applicability and coverage

| Component/evidence | Status and limitation |
|---|---|
| NeMo-Speech.cpp, ggml and NVIDIA patches | Included notices are VERIFIED; exact source/build configuration is UNKNOWN. The broad notice describes MIT and Apache-2.0 patched material. |
| llama.cpp/NMT/TTS | Files exist in the original inventory; applicability to a reduced diarization deployment is UNKNOWN. Suitability for removal has not been established. |
| SentencePiece, Abseil, protobuf, utf8-range | Release license files are VERIFIED. Actual linked revisions and complete static component coverage are UNKNOWN. |
| Darts-clone | ASR contains `darts_clone/darts.h` diagnostics. Separate license text is absent from this ZIP inventory: NEEDS_REVIEW. |
| KenLM/Flashlight | Actual linkage is UNKNOWN; potential LGPL/MIT coverage needs review if present. |
| miniaudio, cpp-httplib, Riva common, optional tokenizers/TLS | Broad source-tree notices describe feature-dependent use. Exact applicability to these DLLs is UNKNOWN. |

The upstream notice identifies SentencePiece revision
`31646a467d2051eb904e0b45de3a73e91fe1c1e3` and describes static bundled
Abseil/protobuf-lite/Darts-clone coverage. ASR embedded diagnostic paths mention
SentencePiece `v0.2.1` and vcpkg `2026.07.29`. These are evidence clues, not a
verified complete version inventory. The preserved SentencePiece LICENSE is
Apache-2.0 text only, without a bundled Darts-clone notice. The existing
Abseil/protobuf files do not establish complete applicable transitive notices.

## KenLM and missing referenced materials — NEEDS_REVIEW

The preserved notice describes KenLM as LGPL 2.1 or later, with file-level
MIT/BSD exceptions. It refers to `LICENSE`, `COPYING`, `COPYING.3` and
`COPYING.LESSER.3`; none is present under the ZIP's license inventory.
Flashlight Text's separate LICENSE is also absent. A notice mentioning KenLM
does not prove inclusion, and absence of `kenlm.dll` does not prove exclusion.

The [pinned root CMake configuration](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/6a3ca369370782acee0dd155e34394ba60b920c4/CMakeLists.txt)
defaults Flashlight/KenLM off and conditionally configures shared KenLM and
static Flashlight. The
[ASR configuration](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/6a3ca369370782acee0dd155e34394ba60b920c4/src/asr/CMakeLists.txt)
links Flashlight conditionally. A disabled-Flashlight diagnostic exists in the
CPU ASR DLL. These support exclusion as an inference, not a confirmed build
configuration. Actual cache, compile definitions, linker inputs and build
receipts are needed to resolve UNKNOWN.

If KenLM is included, specialist review must determine the applicable LGPL
version, corresponding-source and notice requirements, modifications, and
whether users can replace/relink the library. Static inclusion may require
additional relinking materials. No conclusion about satisfaction of these
obligations is made here, and missing terms have not been fabricated.

Other source-tree references absent from the release license inventory include
Riva common, Open JTalk/MeCab/NAIST dictionary, CppJieba/limonp, gguf-py and
OpenSSL. OpenFST/Sparrowhawk/RE2 and container/CUDA notices describe other
feature/platform sets. These absences are VERIFIED inventory facts; their
applicability is UNKNOWN and must not be treated as proof that every component
is incorporated in the CPU candidate. Parakeet and Mandarin attributions
already appear in the preserved broad notice.

## Microsoft runtime — NEEDS_REVIEW

Nine bundled Microsoft DLLs report file version `14.44.35211.0`. The observed
diarization static chain imports `msvcp140.dll`, `vcruntime140.dll`,
`vcruntime140_1.dll` and `vcomp140.dll`, plus Windows system/UCRT libraries.
The exact minimum supported runtime remains UNKNOWN because full compiler
and linker build records are unavailable. PE linker `14.44` and a runtime
file version do not establish those records.

The proposed future strategy is a documented Visual C++ v14 Redistributable
x64 prerequisite installed by the consuming application's deployment.
[Microsoft guidance](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files)
requires applicable Visual Studio redistribution rights and recommends central
deployment for servicing. App-local delivery would require verified authorized
Redistributable files, the applicable REDIST list and publisher servicing.
The existing DLLs are unchanged; no prerequisite was installed.

[Microsoft compatibility rules](https://learn.microsoft.com/en-us/cpp/porting/binary-compat-2015-2017)
require a Redistributable at least as recent as the newest build toolset among
the application components. CPU 14.44 and separately recorded CUDA 14.51 must
be reconciled before selecting an exact common floor. The five extra Microsoft
files are not declared safe to remove. No NuGet restore/install hook is added.

## Requirements before future redistribution

Redistribution requires evidence of actual KenLM linkage;
complete linked component/version and notice coverage, including Darts-clone;
the source/relinking requirements if applicable; Microsoft redistribution
eligibility and runtime floor; and full static/dynamic dependency completeness.
Preserved archive identity alone cannot establish permission for native package
redistribution or satisfy the requirements above.
