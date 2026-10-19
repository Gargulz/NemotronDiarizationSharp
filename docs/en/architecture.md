# Architecture

[Русская версия](../ru/architecture.md) · [Project overview](overview.md)

NemotronDiarizationSharp is an independent .NET wrapper around the official NeMo-Speech.cpp C ABI. The managed layer owns model and stream lifetimes, passes float32 PCM, and exposes the native runtime's speaker probabilities, frame timing, and segments. Neural inference remains in the native engine.

## Projects

- `NemotronDiarizationSharp` — public managed API and native interop.
- `NemotronDiarizationSharp.Runtime.Cpu` — Windows x64 CPU runtime package project. Its local native binaries are excluded from the public repository pending redistribution license review.
- `NemotronDiarizationSharp.Tests` — managed behavior and Windows native inference tests.
- `NemotronDiarizationSharp.Console` — command-line inference example.

## Native boundary and loading

Interop signatures follow the pinned NVIDIA NeMo-Speech.cpp C API. The native library name and entry points are upstream identifiers and remain unchanged. On Windows, a non-empty `NEMOTONDIARIZATIONSHARP_NATIVE_DIR` takes precedence over the application base directory and must be an absolute existing directory containing `nemo_speech_asr_c.dll`; invalid explicit configuration fails without fallback. If it is unset or empty, the wrapper checks the application base directory, then the legacy `NEMOTONSHARP_NATIVE_DIR` directory, and finally the operating system loader.

## Runtime and model

The verified configuration is Windows x64 CPU with the existing pinned runtime and a separately supplied GGUF model. The native test fixture is mono float32 little-endian PCM at 16 kHz. Model weights are not included in NuGet packages. Native binaries are local development inputs and must not be redistributed until licensing review is complete.

Linux, GPU backends, additional audio formats, and clean-consumer package loading have not been verified. The project does not claim those capabilities.

## Resource behavior

The model owns its native model handle and streams own their native stream handles. Both are disposed explicitly in the API and tests. Snapshots expose frame probabilities, frame timestamps, and segments returned by the native API. Native streaming may compact probability history only when it finds a suitable interval where all speakers are silent; this is not guaranteed at a fixed recording duration and does not impose a hard native-memory limit. This native compaction is independent of managed `DiarizationProbabilityHistory` retention limits. Streaming history and overlapping speaker activity otherwise follow the behavior provided by the pinned native runtime.

Managed input validation runs before native Push, so invalid sample rates, rate changes, and non-finite samples leave the stream available for correction. If a native Push or Finish returns an error, or the interop call throws, the stream enters a terminal faulted state because native processing may have changed state before reporting failure. Dispose the stream after such a failure; further pushes, snapshots, frame reads, and Finish calls are rejected. A successful Finish is idempotent. Push after Finish is rejected by the managed API.
