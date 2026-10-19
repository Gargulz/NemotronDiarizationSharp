# Multiple native runtimes

NemotronDiarizationSharp uses NeMo-Speech.cpp's native C API. Build the managed SDK from source and provide a compatible native runtime directory and GGUF model separately. NuGet packages have not been published.

## Default backend selection

`DiarizationModelOptions.Backend` defaults to `DiarizationBackend.Auto`. On Windows x64, Auto selects the staged CUDA runtime when `nvidia-smi` reports an NVIDIA device. Otherwise it selects the CPU runtime when present. A CUDA-only installation with no reported NVIDIA device produces a clear error.

The `nvidia-smi` check is a pre-load availability check, not proof that the installed driver can run every CUDA library or kernel. If CUDA is selected and a later native load, model creation, or inference fails, the process reports that failure and does not switch to CPU. Native libraries may already have been loaded. When `nvidia-smi` cannot report a device and the CPU runtime is installed, Auto chooses CPU before loading either native closure; that policy is covered by managed tests, while a physical no-GPU fallback was not exercised in this environment.

`Backend = DiarizationBackend.Cpu` requires CPU and does not probe or load CUDA. `Backend = DiarizationBackend.Cuda` requires the CUDA package and a device reported by `nvidia-smi`; it never falls back to CPU. Successful model inference is still required to verify a particular host's CUDA compatibility.

The first native initialization fixes one backend for the process. Concurrent initialization is serialized. Later models may use the same backend, including Auto after an explicit selection, but a request to switch between CPU and CUDA throws. Run separate processes to compare backends. `DiarizationModel.SelectedBackend` is null before initialization and reports the process choice afterward.

`GpuDeviceIndex` is a CUDA logical device ordinal; it does not choose a backend. If CUDA is selected and the index is null, ordinal 0 is used. `nvidia-smi` is used only to check whether an NVIDIA device is reported before CUDA loading; CUDA validates the ordinal during model initialization. This avoids treating physical `nvidia-smi` indexes as CUDA ordinals when visibility masks or remapping are in use. Masked and multi-GPU setups remain unverified. A device index combined with forced CPU, or with Auto that selects CPU, is an error.

## Basic example

```csharp
using NemotronDiarizationSharp;

using var model = DiarizationModel.LoadForRecording(
    "models/Nemotron-3-Diarization.q8_0.gguf");

using var stream = model.OpenStream();
// Push float32 PCM samples, finish the stream, and read the result.
```

For a first run, follow the source-reference and explicit CPU runtime setup in the repository README. AllRuntimes describes a separate local aggregation package, not an available public installation. CUDA requires a compatible NVIDIA GPU and driver; CPU requires a compatible Visual C++ x64 runtime including OpenMP. The SDK does not download native files or GGUF model weights.

Use DiarizationBackend.Cpu or DiarizationBackend.Cuda in DiarizationModelOptions to require one backend. LoadForRecording loads the recording profile; call ProcessWaveFile to decode and process a WAV. LoadForStreaming loads the live profile; open a stream, supply PCM chunks, and call Finish. Both profiles use NVIDIA Reference segmentation by default; backend selection does not alter it.

## Runtime packages

- `NemotronDiarizationSharp` contains the managed API and no native binaries.
- `NemotronDiarizationSharp.Runtime.Cpu` carries the CPU native closure.
- `NemotronDiarizationSharp.Runtime.Cuda` carries the CUDA native closure.
- `NemotronDiarizationSharp.AllRuntimes` is a convenience metapackage depending on all three packages.

The runtime packages place assets under distinct output folders:

```text
runtimes/nemotron/cpu/win-x64/native/
runtimes/nemotron/cuda/win-x64/native/
```

Standalone .NET 10 consumers were restored and published with CPU, CUDA, both packages, and AllRuntimes from a local feed. Module inspection confirmed the CPU and CUDA copies of the colliding DLL names load from their own folders. The validated runtime scope is Windows x64; Linux x64 remains unverified.

CUDA needs a compatible NVIDIA GPU and driver plus the dependencies expected by the staged CUDA runtime. The native execution tests completed recording and streaming on an NVIDIA GPU. Native scheduler diagnostics reported a `CUDA0` compute buffer, while the process supervisor correctly records direct kernel execution as unknown; this is not a guarantee for other GPUs or drivers.

Model weights remain external. Native runtime packages have not been published; third-party binary redistribution and provenance review remain incomplete. A successful local package restore does not grant redistribution rights.

See [release readiness](release-readiness.md) for the package metadata audit, native component inventory, evidence classification, and outstanding redistribution requirements.
