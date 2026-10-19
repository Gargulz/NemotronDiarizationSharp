# Pinned upstream license evidence

This repository contains a scoped upstream source snapshot. Some relative paths in the preserved upstream notice documents refer to files in the complete upstream tree and therefore do not resolve within this partial snapshot. The notice and license texts are retained unchanged; pinned source references and local copies identify the available materials. A missing source-tree path does not mean that the corresponding notice text has been removed.


Native binary redistribution review remains incomplete. This document
separates source licensing, preserved notices, observed link inputs, and actual
binary provenance. It does not change the managed SDK's MIT license or grant
rights to additional components, and it does not provide legal clearance.
Vendor-specific CUDA/Microsoft terms are assessed separately in
[vendor-redistribution.md](vendor-redistribution.md).

## Evidence and text identity

NeMo-Speech.cpp v0.2.0 is pinned to
[`6a3ca369370782acee0dd155e34394ba60b920c4`](https://github.com/NVIDIA/NeMo-Speech.cpp/tree/6a3ca369370782acee0dd155e34394ba60b920c4).
Its primary documents are the pinned
[LICENSE](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/6a3ca369370782acee0dd155e34394ba60b920c4/LICENSE),
[NOTICE](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/6a3ca369370782acee0dd155e34394ba60b920c4/NOTICE), and
[THIRD_PARTY_NOTICES.md](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/6a3ca369370782acee0dd155e34394ba60b920c4/THIRD_PARTY_NOTICES.md).
The notices expressly cover the **source tree**. Their component list is not
a software bill of materials for either packaged binary set.

OBSERVED: all three CPU archive notice files equal the official pinned HTTPS
texts after CRLF-to-LF normalization. Their bytes also equal the corresponding
files in the existing local CUDA source directory. The archive versions are
retained unchanged; differences from the scoped pinned reference snapshot
are line endings. SHA-256 values for those preserved archive files are:

| File | SHA-256 |
|---|---|
| `LICENSE` | `6EB0D1526EF4D4E4F5516C6D679EB148AEFC36D0D2DF9D9CC1FA048FA287963C` |
| `NOTICE` | `0F55F591903FE80A07F8B1331029983B2B9F460967CCFB8DBE60F958958BEC25` |
| `THIRD_PARTY_NOTICES.md` | `3B3163AB04D77BD137F5823D5DDA5328448F43FE34ED7780EDC8148B62B454C6` |

The archive license includes the NVIDIA copyright, Apache-2.0 terms, and an
external-materials disclaimer; none should be stripped. Apache-2.0 section 4
requires distributing the license, marking modified files, retaining relevant
source notices, and carrying applicable NOTICE attributions. These requirements
do not license the independent managed SDK or other vendors' components.

## Components, applicability, and obligations

| Component and primary evidence | Observed applicability | Distribution status and requirements |
|---|---|---|
| NeMo-Speech.cpp; pinned LICENSE/NOTICE above | CPU engine DLLs match the preserved release archive. CUDA engine DLLs match the existing local build outputs; source/build evidence is separate. | Source terms VERIFIED / DOCUMENTED. Preserve full LICENSE, NOTICE and applicable third-party notices. Complete binary provenance is still MISSING EVIDENCE. |
| ggml / llama.cpp; [MIT at gitlink `bd4f514db14d87fded667787a7a963bfbaa98e89`](https://github.com/ggml-org/llama.cpp/blob/bd4f514db14d87fded667787a7a963bfbaa98e89/LICENSE) | Separate ggml DLLs are packaged. The packaged CPU set contains no `llama.dll`. CUDA source/build records identify the gitlink and a patched ggml tree; source cleanliness is not an authenticated build receipt. | MIT notice VERIFIED / DOCUMENTED. Retain copyright (c) 2023-2026 The ggml authors and the permission/warranty text. NVIDIA Apache-2.0 additions and existing MIT code both retain their notices. Do not assign only Apache-2.0 to this combination. |
| SentencePiece; [v0.2.1 LICENSE](https://github.com/google/sentencepiece/blob/v0.2.1/LICENSE) and [source CMake](https://github.com/google/sentencepiece/blob/v0.2.1/src/CMakeLists.txt) | CPU ASR diagnostics identify v0.2.1 but exact static version is unproven. Existing CUDA link records name `sentencepiece.lib`; vcpkg SPDX and build records identify 0.2.1 with static library triplet. | Apache-2.0 text VERIFIED / DOCUMENTED; preserve existing text and applicable attributions. CUDA build inputs support static inclusion, but do not establish every retained object in the shipped DLL. |
| Abseil; [20260107.1 LICENSE](https://github.com/abseil/abseil-cpp/blob/20260107.1/LICENSE) | Existing CUDA link records list static `absl` libraries; vcpkg records identify 20260107.1#3. CPU exact revision remains UNKNOWN. | Apache-2.0 text VERIFIED / DOCUMENTED. Preserve it; a source/build component audit must still check applicable file-level notices. |
| Protocol Buffers; [v33.4 LICENSE](https://github.com/protocolbuffers/protobuf/blob/v33.4/LICENSE) | CUDA `libprotobuf.lib` input; vcpkg version 6.33.4#2 maps to upstream v33.4. CPU exact revision remains UNKNOWN. | BSD 3-Clause text VERIFIED / DOCUMENTED. Reproduce copyright, conditions, disclaimer; no endorsement. Do not substitute the v21.12 text-normalization version listed for other upstream platforms. |
| utf8-range; [protobuf v33.4 LICENSE](https://github.com/protocolbuffers/protobuf/blob/v33.4/third_party/utf8_range/LICENSE) | CUDA `utf8_validity.lib` input; vcpkg 6.33.4. CPU has a matching notice, but exact binary revision is UNKNOWN. | MIT text VERIFIED / DOCUMENTED. Preserve Yibo Cai and Google copyright statements and permission text. |
| Darts-clone; [SentencePiece v0.2.1 LICENSE](https://github.com/google/sentencepiece/blob/v0.2.1/third_party/darts_clone/LICENSE) | CPU ASR diagnostic paths mention `darts_clone/darts.h`. CUDA dependency source `normalizer.cc` includes that header and is in SentencePiece runtime sources; exact retained objects remain unverified. | Missing text supplied: [DARTS_CLONE_LICENSE.txt](licenses/DARTS_CLONE_LICENSE.txt). Notice remedy VERIFIED / DOCUMENTED; exact CPU composition remains MISSING EVIDENCE. Binary distributions must reproduce the BSD copyright, conditions and disclaimer; no endorsement. |
| KenLM / Flashlight Text; pinned NVIDIA notice and conditional CMake links below | CPU linkage UNKNOWN. Existing CUDA configuration records disable Flashlight, and inspected CUDA link inputs contain neither KenLM nor Flashlight; this is build-configuration evidence for that local build. | CPU MISSING EVIDENCE; NEEDS LEGAL REVIEW if LGPL code is present. A broad notice or absent standalone DLL cannot decide static inclusion. See the conditional obligations below. |

The installed CUDA vcpkg copyright files for SentencePiece, Abseil, protobuf,
and utf8-range are byte-for-byte identical to the four preserved CPU archive
files. Their reuse supplies verified terms without inventing duplicate grants:

| Existing path below `src/NemotronDiarizationSharp.Runtime.Cpu/licenses/` | CUDA installed copyright / existing CPU SHA-256 |
|---|---|
| `third_party/vcpkg/sentencepiece/LICENSE` | `CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30` |
| `third_party/vcpkg/abseil/LICENSE` | `C79A7FEA0E3CAC04CD43F20E7B648E5A0FF8FA5344E644B0EE09CA1162B62747` |
| `third_party/vcpkg/protobuf/LICENSE` | `6E5E117324AFD944DCF67F36CF329843BC1A92229A8CD9BB573D7A83130FEA7D` |
| `third_party/vcpkg/utf8-range/LICENSE` | `02DE69B64FC36D9E938F418E52723E42F0B2B226D58A9CB3C8DCBDF7059F5074` |

Local evidence root:
the local CUDA build evidence.
The relevant installed files are below
`build/cuda-diar/vcpkg_installed/x64-windows-static-md/share/<component>/`.
Each directory has `copyright` and `vcpkg.spdx.json`; the latter identifies
upstream source URLs/checksums and the vcpkg port revision. These are local
build records, not independently authenticated release attestations.

## New Darts-clone text provenance

OBSERVED: the newly included text was retrieved as bytes from the official
[SentencePiece v0.2.1 raw license](https://raw.githubusercontent.com/google/sentencepiece/v0.2.1/third_party/darts_clone/LICENSE).
It is byte-identical to the existing CUDA dependency source copy at
`dependencies/vcpkg-2026.07.29/buildtrees/sentencepiece/src/v0.2.1-3e6e94c790.clean/third_party/darts_clone/LICENSE`.
It also equals the official raw text at the
[SentencePiece revision cited by NVIDIA](https://raw.githubusercontent.com/google/sentencepiece/31646a467d2051eb904e0b45de3a73e91fe1c1e3/third_party/darts_clone/LICENSE).

- File: `licenses/DARTS_CLONE_LICENSE.txt`.
- SHA-256: `155F59997298EE336602C49F9C1110F268AC394CA2197EB02647A3555935AD52`.
- Copyright: `(c) 2008-2011, Susumu Yata`, as supplied upstream.
- The literal `<ORGANIZATION>` in the upstream non-endorsement condition is
  preserved. No holder, year or clause was invented or replaced.

This addition fixes the absent notice text. It does not prove that the pinned
SentencePiece source revision built the CPU release, or that all other static
dependency notices are complete.

## ggml file-level attributions

The existing local patched ggml tree also contains file-level MIT material
that is not individually named in the broad NVIDIA notice:

- [llamafile tinyBLAS `sgemm.cpp`](https://github.com/ggml-org/llama.cpp/blob/bd4f514db14d87fded667787a7a963bfbaa98e89/ggml/src/ggml-cpu/llamafile/sgemm.cpp)
  begins with `Copyright 2024 Mozilla Foundation` and a complete MIT grant.
- [CPU `ops.cpp`](https://github.com/ggml-org/llama.cpp/blob/bd4f514db14d87fded667787a7a963bfbaa98e89/ggml/src/ggml-cpu/ops.cpp)
  and [CUDA `rope.cu`](https://github.com/ggml-org/llama.cpp/blob/bd4f514db14d87fded667787a7a963bfbaa98e89/ggml/src/ggml-cuda/rope.cu)
  identify YaRN material as MIT licensed, copyright (c) 2023 Jeffrey Quesnelle
  and Bowen Peng.

[GGML_FILE_NOTICES.txt](licenses/GGML_FILE_NOTICES.txt) preserves the local
source header/attribution verbatim and includes the existing ggml MIT license.
The Mozilla header was also inspected in the official pinned source. Existing
CUDA cache has `GGML_LLAMAFILE=ON`; build.ninja names `sgemm.cpp`, CPU `ops.cpp`
and CUDA `rope.cu` objects as build/link inputs. This supports notice inclusion
without proving every function survives linking. Exact CPU retained source
composition remains UNKNOWN; conservative notice retention does not clear it.
The separately visible Arm KleidiAI source is disabled in the existing CUDA
cache (`GGML_CPU_KLEIDIAI=OFF`); no Arm backend inclusion is claimed for x64.

GGML_FILE_NOTICES.txt SHA-256:
`2BE8F07359079C7AB9FC4BBF61F99FB90D266ECA09420950E03A8D92760A3CD4`.

## KenLM: conditional review, not assumed inclusion

The pinned
[root CMakeLists.txt](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/6a3ca369370782acee0dd155e34394ba60b920c4/CMakeLists.txt)
defaults `NEMO_SPEECH_WITH_FLASHLIGHT` to OFF. When enabled, it requests shared
KenLM runtime libraries and static Flashlight Text. The
[runtime source allowlist](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/6a3ca369370782acee0dd155e34394ba60b920c4/cmake/kenlm_runtime.cmake)
excludes AT&T-licensed `util/getopt.*`; the
[ASR target](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/6a3ca369370782acee0dd155e34394ba60b920c4/src/asr/CMakeLists.txt)
links the Flashlight libraries conditionally. Defaults and source exclusions
describe an intended build and cannot independently certify the CPU DLL.

The pinned NVIDIA third-party notice declares most KenLM runtime files LGPL
2.1 or later and lists MIT/BSD file exceptions. Official pinned repository tree
metadata identifies KenLM gitlink `4cb443e60b7bf2c0ddf3c745378f76cb59e254e5`
and Flashlight Text gitlink `49e163ab1e7b8108922512c294ab8513b89f404c`.
The exact KenLM
[LICENSE](https://github.com/kpu/kenlm/blob/4cb443e60b7bf2c0ddf3c745378f76cb59e254e5/LICENSE)
and [COPYING (LGPL 2.1)](https://github.com/kpu/kenlm/blob/4cb443e60b7bf2c0ddf3c745378f76cb59e254e5/COPYING)
were retrieved and inspected; the latter's sections 4-6 support the conditional
requirements below. Flashlight's separate
[MIT license](https://github.com/flashlight/text/blob/49e163ab1e7b8108922512c294ab8513b89f404c/LICENSE)
does not override KenLM's LGPL. If applicable code is included,
the release must identify the exact source/revision, modifications and license
option. A qualified reviewer must assess notices and license copies,
corresponding library source delivery or permitted offer, modification rights,
reverse-engineering terms for debugging modifications, and replacement or
relinking materials/mechanism under LGPL 2.1 sections 4-6. Static and shared
linking have different compliance routes; neither is automatically cleared.
Existing CPU materials lack the separate KenLM and Flashlight terms, source
offer, and relinking evidence. Merely adding LGPL text would not close this gap.

## Other upstream-listed components

The preserved broad upstream notice already supplies MIT attributions for
parakeet.cpp (revision `1675ee5b612b629645e3bb4b42f671da27a76d5b`)
and source-derived material. Keep that notice. Optional components are not all
runtime dependencies of these packages:

| Upstream-listed material | Applicability to selected native package files |
|---|---|
| miniaudio 0.11.25, public-domain/MIT-0 alternatives | Source notice identifies command-line microphone capture. No CLI is packaged. Existing notice retained conservatively; no separate inclusion claim. |
| cpp-httplib (MIT), OpenSSL (Apache-2.0), Riva common (MIT) | Source feature dependencies for HTTP/TLS/gRPC. CUDA build configuration disables those features; CPU archive is broader than the five selected DLLs and does not prove their internal absence. |
| Open JTalk/MeCab/NAIST dictionary (BSD), CppJieba/limonp and Mandarin data (MIT) | Source notice describes optional TTS/tokenizer material. No TTS DLL or dictionary is in the selected package inventories; these optional features are disabled in existing CUDA configuration. Do not interpret missing standalone notices as proof of CPU static exclusion. |
| OpenFST/Sparrowhawk (Apache-2.0), protobuf v21.12/RE2 (BSD), Linux system libraries | Source notice describes text normalization on Linux/macOS or a Linux container. Neither platform/container/text-normalization library is shipped in these Windows package sets; their container-specific distribution is NOT APPLICABLE here. |
| gguf-py and whisper.cpp `jfk.wav` (MIT) | Model-conversion tooling / audio fixture; neither is packaged. NOT APPLICABLE to current NuGet contents. |
| esaxx in SentencePiece source | Trainer dependency is separate from the SentencePiece runtime target in v0.2.1 source CMake. The observed CUDA input is `sentencepiece.lib`, not `sentencepiece_train.lib`; no trainer delivery is observed. This does not inventory all object-level provenance. |
| Model weights | Remain external to all four packages. Their own terms must be evaluated separately by the consumer; native/source licenses do not cover them. |

No missing optional license has been fabricated or silently treated as included.
CPU build receipts or an authoritative binary composition statement remain
necessary to resolve the CPU-specific uncertainty. CUDA records provide more
specific build inputs, but leave source cleanliness and final linked composition
as explicit evidence limits.
