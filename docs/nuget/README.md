# NemotronDiarizationSharp

NemotronDiarizationSharp is an unofficial .NET SDK for NVIDIA Nemotron speaker diarization. It returns speaker-labeled time segments and preserves overlapping speakers. Inference runs in the native NeMo-Speech.cpp engine.

## Native runtime and model required

The NuGet package contains the managed API only. Before running inference, provide a compatible native runtime **and** a GGUF model separately. Neither is downloaded or included in this package. The runtime package projects are not published on NuGet.

- **CPU:** provision the Windows x64 CPU runtime and its dependencies. A compatible Microsoft Visual C++ x64 Redistributable, including OpenMP, is required.
- **CUDA:** provision the Windows x64 CUDA runtime and its dependencies, plus a compatible NVIDIA GPU and driver. `nvidia-smi` visibility does not guarantee that a particular driver or GPU can run inference.
- Set `NEMOTONDIARIZATIONSHARP_NATIVE_DIR` to the absolute directory containing the selected runtime DLLs before starting the application. Select CPU or CUDA explicitly when you need a specific backend.
- Model weights remain external and are subject to their own terms.

For runtime setup, see the [CPU runtime notes](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/src/NemotronDiarizationSharp.Runtime.Cpu/README.md), [CUDA runtime notes](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/src/NemotronDiarizationSharp.Runtime.Cuda/README.md), and [runtime directory selection guide](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/en/multi-runtime.md). The CPU runtime can be provisioned from the [official NeMo-Speech.cpp v0.2.0 release](https://github.com/NVIDIA/NeMo-Speech.cpp/releases/tag/v0.2.0); CUDA users need a separately prepared compatible CUDA build following the [upstream v0.2.0 build guide](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/v0.2.0/README.md#build-from-source). These links describe provisioning and prerequisites; they do not grant redistribution rights. For models, see the [project workflow guidance](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/en/nemotron3-reference-workflows.md) and [NVIDIA Nemotron 3 model card](https://huggingface.co/nvidia/Nemotron-3-Diarization). Review [third-party licensing and distribution status](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/distribution/DISTRIBUTION_STATUS.md) before distributing native assets.

## Install

Requires .NET 10. This prerelease has been validated on Windows x64; Linux and other platforms are unverified.

```powershell
dotnet add package NemotronDiarizationSharp --version 0.1.0-preview.2
$env:NEMOTONDIARIZATIONSHARP_NATIVE_DIR = '<absolute path to the CPU runtime directory>'
```

## Process a WAV recording

The example uses an existing compatible model and a supported RIFF/WAVE file. The SDK reads PCM 8/16/24/32-bit or IEEE float32 WAV, averages channels to mono, and accepts sample rates from 8 to 96 kHz.

```csharp
using NemotronDiarizationSharp;

using var model = DiarizationModel.LoadForRecording("model.gguf",
    new DiarizationModelOptions { Backend = DiarizationBackend.Cpu });
var result = model.ProcessWaveFile("meeting.wav");

foreach (DiarizationSegment segment in result.Segments)
    Console.WriteLine($"{segment.StartTime:F2}-{segment.EndTime:F2}s: speaker {segment.Speaker}");

DiarizationPostprocessor.WriteRttm(Console.Out, result.Segments, "meeting");
```

Compressed audio, RF64, RIFX, and WAVE_FORMAT_EXTENSIBLE are unsupported; decode or convert those formats before calling the SDK. Recording mode retains probability history for final postprocessing, so memory use grows with recording duration.

## Streaming audio

For live or long-running input, use `DiarizationModel.LoadForStreaming`, open a stream with `OpenStream`, push sequential mono float32 PCM chunks with their sample rate, then call `Finish`. Dispose both the stream and model. Incremental updates may revise earlier results; use the final snapshot after `Finish` for completed diarization. See the [streaming API guide](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/en/sdk-guide.md#asynchronous-pcm-input) and [recording/live workflow notes](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/en/nemotron3-reference-workflows.md).

## Troubleshooting

- **`DllNotFoundException` or `BadImageFormatException`:** confirm the native directory has the selected Windows x64 runtime, all runtime dependencies, and the required VC++ runtime for CPU. Check that no x86 DLLs are present.
- **CUDA device or initialization error:** verify the NVIDIA driver, GPU visibility, CUDA runtime dependencies, and model compatibility. CUDA selection does not fall back to CPU after native loading begins.
- **Model load error:** check that the GGUF file exists and is a model supported by the pinned NeMo-Speech.cpp runtime. Model contents are validated by the native engine.
- **Audio format error:** use a supported RIFF/WAVE encoding or decode the source to mono float32 PCM. See the [SDK guide](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/en/sdk-guide.md) for API details and error behavior.

## Licensing

The original managed SDK source is MIT licensed. That license does not cover native runtime binaries, third-party components, or model weights. Review the [distribution status](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/distribution/DISTRIBUTION_STATUS.md), [native provenance](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/distribution/native-provenance.md), [upstream license evidence](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/distribution/upstream-licenses.md), and [vendor redistribution evidence](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/distribution/vendor-redistribution.md) before distributing native assets.
