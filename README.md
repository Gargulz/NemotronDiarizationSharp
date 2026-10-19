# NemotronDiarizationSharp

[Русская версия](README.ru.md)

An independent, unofficial .NET SDK for speaker diarization: identify who spoke when in a recording or a stream, preserve overlapping speakers, and export RTTM. Neural inference runs through the native C API of [NVIDIA NeMo-Speech.cpp](https://github.com/NVIDIA/NeMo-Speech.cpp). This project is not affiliated with or endorsed by NVIDIA.

## Requirements and availability

- .NET 10; locally validated on Windows x64 with CPU and CUDA runtimes pinned to NeMo-Speech.cpp v0.2.0. Linux and Vulkan are unverified; broad GPU/driver compatibility is unverified.
- Supply a compatible native runtime directory with its dependencies and a GGUF model separately. Neither is downloaded or included in the managed SDK. CPU requires a compatible Visual C++ x64 runtime including OpenMP; CUDA also requires a compatible NVIDIA GPU and driver.
- No NuGet packages have been published. Build the SDK from source. Native runtime redistribution review remains incomplete; the SDK's MIT license does not grant rights to third-party binaries or model weights.

See [runtime provisioning and selection](docs/en/multi-runtime.md), [CPU component requirements](src/NemotronDiarizationSharp.Runtime.Cpu/README.md), and [distribution details](docs/distribution/DISTRIBUTION_STATUS.md).

## Quick start

From the repository root, create an application and reference the managed SDK source:

```powershell
dotnet new console -n DiarizationDemo -o ../DiarizationDemo -f net10.0
dotnet add ../DiarizationDemo/DiarizationDemo.csproj reference src/NemotronDiarizationSharp/NemotronDiarizationSharp.csproj
$env:NEMOTONDIARIZATIONSHARP_NATIVE_DIR = '<absolute-cpu-runtime-directory>'
```

Replace the generated `Program.cs` with this example and supply existing model/audio paths:

<!-- compile-example: en-recording; scaffold=recording -->
```csharp
using NemotronDiarizationSharp;

using var model = DiarizationModel.LoadForRecording("model.gguf",
    new DiarizationModelOptions { Backend = DiarizationBackend.Cpu });
DiarizationSnapshot result = model.ProcessWaveFile("meeting.wav");
DiarizationPostprocessor.WriteRttm(Console.Out, result.Segments, "meeting");
```

```powershell
dotnet run --project ../DiarizationDemo/DiarizationDemo.csproj -c Release
```

The SDK reads little-endian RIFF/WAVE PCM 8/16/24/32-bit and IEEE float32 audio at 8–96 kHz, averaging channels to mono. MP3, compressed WAV, WAVE_FORMAT_EXTENSIBLE, RF64 and RIFX require external conversion. Decoded mono float32 PCM can also be supplied directly. The recording workflow retains probability history for final postprocessing, so memory use grows with recording length.

## Streaming and backends

Use `LoadForStreaming`, `OpenStream`, sequential PCM pushes and `Finish` for live input. Updates can revise earlier results; dispose both model and stream. See [recording and streaming workflows](docs/en/nemotron3-reference-workflows.md) and the [SDK guide](docs/en/sdk-guide.md).

`Backend` defaults to `Auto`: it prefers staged CUDA assets when `nvidia-smi` reports a device, otherwise CPU when available. Forced CPU/CUDA selection requires that backend. The first native initialization fixes one backend per process; a later native load failure does not switch backends.

## Build and verify

The managed SDK builds without a native runtime or model:

```powershell
dotnet build src/NemotronDiarizationSharp/NemotronDiarizationSharp.csproj -c Release -m:1
```

See [CONTRIBUTING](CONTRIBUTING.md#validation) for solution build and managed test commands. Managed tests do not establish native inference or package redistribution readiness.

## Documentation

- [SDK examples and API contracts](docs/en/sdk-guide.md)
- [Recording/live workflows](docs/en/nemotron3-reference-workflows.md)
- [CPU/CUDA selection and deployment](docs/en/multi-runtime.md)
- [Project overview](docs/en/overview.md) and [architecture](docs/en/architecture.md)
- [Package availability and third-party terms](docs/distribution/DISTRIBUTION_STATUS.md)

## License and acknowledgments

Original project-owned source is [MIT licensed](LICENSE), copyright Gargulz. Third-party sources, native components and model weights retain their own terms and notices.

[Whisper.net](https://github.com/sandrohanea/whisper.net) was an inspiration. Development also used [OpenAI Codex](https://openai.com/codex/).
