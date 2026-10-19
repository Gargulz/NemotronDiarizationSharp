# SDK guide

Detailed API examples, CLI and opt-in diagnostics. Commands run from the repository root; supply native runtime and model inputs separately. For the recording entry point, see [Quick start](../../README.md#quick-start).

## Advanced decoded PCM example

The SDK accepts **mono float32 PCM samples** with an explicit sample rate from 8,000 to 96,000 Hz. Samples must be finite and should be normalized to `[-1, 1]`; the SDK also reads supported RIFF/WAVE files through DiarizationWaveReader and ProcessWaveFile. Other codecs and containers require external decoding. The native runtime resamples the supplied PCM to the model's rate.

For an advanced decoded PCM workflow, the following example uses stateless full-attention inference. Pass the model and compatible native runtime paths from your application; decode audio to mono float32 PCM first:

<!-- sdk-example: consumer-api -->
```csharp
static DiarizationSnapshot ProcessConsumerPcm(
    string modelPath,
    string nativeRuntimeDirectory,
    ReadOnlySpan<float> monoPcm,
    int sampleRate,
    string? rttmPath = null)
{
    Environment.SetEnvironmentVariable("NEMOTONDIARIZATIONSHARP_NATIVE_DIR",
        Path.GetFullPath(nativeRuntimeDirectory));
    using var model = DiarizationModel.Load(Path.GetFullPath(modelPath),
        new DiarizationModelOptions { Backend = DiarizationBackend.Cpu });
    DiarizationSnapshot result = model.ProcessOffline(monoPcm, sampleRate);

    if (result.FrameCount > 0)
        Console.WriteLine($"Frame 0 at {result.GetFrameTimestamp(0):F2}s: " +
            $"speaker channel 0 probability {result.GetProbability(0, 0):F3} of {result.SpeakerCount}");
    foreach (DiarizationSegment segment in result.Segments)
        Console.WriteLine($"{segment.StartTime:F2}-{segment.EndTime:F2}: speaker {segment.Speaker}");

    if (rttmPath is not null)
    {
        using var writer = File.CreateText(rttmPath);
        DiarizationPostprocessor.WriteRttm(writer, result.Segments, recordingId: "recording");
    }
    return result;
}
```

`DiarizationSnapshot` exposes raw probabilities by zero-based frame and speaker-channel index, plus frame timestamps; ready-to-use segments use one-based speaker IDs. The `using` declaration disposes the native model. For long recordings, use the streaming API below and finish the stream explicitly. The managed SDK package contains neither native DLLs nor GGUF weights; provision both separately. CPU and CUDA inference were validated on Windows x64 with NeMo-Speech.cpp v0.2.0, but this does not establish compatibility with other GPUs or drivers. Linux remains unverified, and redistribution rights have not been cleared.

`ProcessOffline` handles one complete input and is limited by the model's positional capacity. For long recordings, feed chunks to a stream and finish it explicitly:

<!-- compile-example: en-streaming-processing; scaffold=streaming -->
```csharp
using var model = DiarizationModel.Load(modelPath);
using var stream = model.OpenStream();

foreach (ReadOnlyMemory<float> chunk in GetMonoPcmChunks())
    stream.Push(chunk.Span, sampleRate);

stream.Finish();
DiarizationSnapshot final = stream.GetSnapshot();
foreach (DiarizationSegment segment in final.Segments)
    Console.WriteLine($"{segment.StartTime:F2}-{segment.EndTime:F2}: speaker {segment.Speaker}");
```

`GetSnapshot()` does not consume the incremental probability cursor. Poll `ReadNewFrames()` when you need probabilities as processing proceeds; read often enough to avoid a `DiarizationFrameHistoryGapException` after native history compaction. Frame probability indices are zero-based; `DiarizationSegment.Speaker` uses native one-based speaker IDs. `Backend` defaults to `Auto`; `GpuDeviceIndex` chooses a CUDA device and does not choose the backend. Null uses CUDA device 0 when CUDA is selected.

The model and stream own native resources and should be disposed, as in the examples. The stream retains the native model until it is disposed, so disposing the managed model first does not close an active stream. Do not concurrently use or dispose an instance while another operation is active; the API does not promise general thread safety. A missing model file throws `FileNotFoundException`; invalid rates or non-finite samples throw argument exceptions; native call failures are surfaced as `InvalidOperationException` with the native error message. Catch `DiarizationFrameHistoryGapException` before its `InvalidOperationException` base when reading incremental frames. Missing or incompatible native DLLs can surface as `DllNotFoundException` or `BadImageFormatException`.

## CPU and CUDA selection

The default `DiarizationModel.Load(modelPath)` uses Auto backend selection and the native default streaming geometry. Auto prefers CUDA when the CUDA runtime is installed and `nvidia-smi` reports a device; otherwise it selects the CPU runtime if installed. To require CPU, set the backend explicitly:

<!-- compile-example: en-cpu-selection; scaffold=model-path -->
```csharp
using var model = DiarizationModel.Load(modelPath,
    new DiarizationModelOptions { Backend = DiarizationBackend.Cpu });
```

To explicitly select CUDA, use this in a process staged with the CUDA-enabled native runtime:

<!-- compile-example: en-cuda-selection; scaffold=model-path -->
```csharp
using var model = DiarizationModel.Load(modelPath,
    new DiarizationModelOptions { Backend = DiarizationBackend.Cuda, GpuDeviceIndex = 0 });
```

A nonnegative GPU index chooses that CUDA device but does not select CUDA by itself. `Backend = Cuda` requires CUDA and never falls back; `Backend = Cpu` requires CPU. Auto uses a pre-load `nvidia-smi` device check, which does not prove driver or kernel compatibility. If native loading begins and fails, the process does not switch runtimes. The first native initialization fixes the process backend; run separate processes to use both. `Preset` values (`streaming`, `offline`, `v3-streaming`, and `v3-offline`) configure streaming geometry; the names `offline` and `v3-offline` do not select `ProcessOffline`.

`NEMOTONDIARIZATIONSHARP_NATIVE_DIR`, when set, must be an absolute directory containing the selected runtime's entry DLL and dependencies. Otherwise the loader checks the separate CPU and CUDA package subdirectories under the application directory. The selected C ABI entry DLL is loaded by absolute path with dependency lookup scoped to its directory and System32. CPU and CUDA are never switched after native loading begins, and only one backend is selected per process. Linux support remains unverified. Native binaries are not included in the managed SDK package or repository, and redistribution rights have not been cleared.

The Windows x64 native inference test uses a local GGUF model and mono float32 little-endian PCM fixture. Set `NEMOTONDIARIZATIONSHARP_MODEL` and `NEMOTONDIARIZATIONSHARP_PCM_F32LE` to their paths. The old `NEMOTONSHARP_MODEL` and `NEMOTONSHARP_PCM_F32LE` names remain supported as fallbacks. To load native libraries from a custom directory, set `NEMOTONDIARIZATIONSHARP_NATIVE_DIR`; `NEMOTONSHARP_NATIVE_DIR` remains a compatibility fallback. The console example supports the WAV-to-RTTM workflow below and retains the positional raw PCM invocation.

## WAV to RTTM console

After building the console in Release, supply an existing GGUF model, WAV file and compatible native runtime with its dependencies. The output directory must already exist.

```powershell
dotnet run --project examples/NemotronDiarizationSharp.Console -c Release --no-build --no-restore -- --model '<model.gguf>' --input '<audio.wav>' --output '.\result.rttm' --backend cpu --native-dir '<absolute-cpu-runtime-directory>'
dotnet run --project examples/NemotronDiarizationSharp.Console -c Release --no-build --no-restore -- --model '<model.gguf>' --input '<audio.wav>' --output '.\result.rttm' --backend cuda --gpu-device 0 --native-dir '<absolute-cuda-runtime-directory>'
```

`--native-dir` is normalized to an existing absolute directory and sets `NEMOTONDIARIZATIONSHARP_NATIVE_DIR` before model loading. Alternatively, set that process environment variable before starting the console. Use a compatible CPU runtime for CPU and a separately prepared CUDA runtime for CUDA; backend selection never falls back. Runtime selection is cached on first native resolution as described above. The console does not download a runtime or model.

| Option | Behavior |
| --- | --- |
| `--model`, `--input`, `--output` | Required model, WAV and RTTM paths. Model existence/nonempty checks precede loading; model contents and compatibility are validated by the native SDK. |
| `--backend cpu\|cuda` | Default `cpu`; CUDA defaults to device 0. |
| `--gpu-device index` | Nonnegative integer, allowed only with CUDA. |
| `--native-dir directory` | Optional explicit native runtime directory. |
| `--segmentation native\|managed` | Default `native`. |
| `--recording-id token` | Default input filename without extension; whitespace/control characters become `_`, or `recording` if empty. Explicit tokens must be nonempty without whitespace/control characters. |
| `--chunk-frames count` | Decoded mono audio frames per push, integer 1..96000; default one second at the source rate. This does not set SDK encoder geometry. |
| `--help` | Standalone usage display, without loading files or native code. |

Options use separate values; unknown/duplicate options and invalid values fail. Exit codes are `0` for success/help, `2` for options or preflight input failures, and `1` for processing/runtime/export failures. Errors preserve the underlying diagnostic, including missing native dependencies, incompatible images, entry points and runtime versions.

The SDK WAV reader reads seekable regular files in little-endian RIFF/WAVE format: unsigned PCM8, signed PCM16/24/32, or IEEE float32, with at least one channel and a source rate of 8000-96000 Hz. Audio must be nonempty with complete frames and exactly one `fmt ` and one `data` chunk; declared RIFF, chunk, padding, alignment and byte-rate bounds are checked. Unknown chunks are skipped; bytes outside the declared RIFF are ignored. Compressed audio, WAVE_FORMAT_EXTENSIBLE, RF64 and RIFX are unsupported. Channels are downmixed by their arithmetic mean using a double accumulator. WAV float NaN/infinity is rejected; finite channel values are clamped to `[-1,1]` before averaging. The console forwards the source sample rate without managed resampling; the native frontend handles its model rate.

Preflight validates the entire audio in a bounded first pass before `DiarizationModel.Load`, then reopens it for a second sequential decoding/streaming pass. It does not allocate the whole PCM recording. Native mode exports final native snapshot segments after `Finish()`. Managed mode polls strict `ReadResultUpdates()` after every push and finish, applies observed revisions to full `DiarizationProbabilityHistory(null)`, verifies coverage from frame zero through the final frame and calls managed `Segment`. A history gap fails instead of exporting incomplete history. Managed full probability retention and the final owned copy grow with recording duration; bounded PCM chunks do not imply constant total memory. Native compaction can hide older revisions, and complete captured coverage is not a stability guarantee. Native and managed segmentation can differ.

Final output uses SDK `WriteRttm`, preserving overlaps and absolute frame timing, with channel 1 and one-based `speaker_N` IDs. Model frame cadence can extend final segment timing beyond audio duration; the console does not trim or shift timestamps. RTTM is written to a temporary file in the destination directory and atomically replaces the destination only after processing/export succeeds; existing output is preserved on failure. Output must differ from model/input paths. Model, input and output files and all their ancestor directories must not contain symbolic links, junctions or reparse points. Do not change these paths during a run; checks do not provide isolation from concurrent filesystem changes.

The legacy invocation remains `NemotronDiarizationSharp.Console <model.gguf> <mono-pcm-f32le.raw> <sample-rate>`. It uses bounded sequential reads and prints final native segments to stdout. Raw float32 little-endian data must be nonempty, aligned to four bytes and finite; finite amplitudes pass through unchanged. Its rate range is also 8000-96000 Hz.

## Opt-in long-run diagnostics

`tests/NemotronDiarizationSharp.LongRun` is a separate console harness and does not run as part of `dotnet test`. It verifies the pinned local model and 8-second PCM fixture by size and SHA-256, cycles that fixture through bounded PCM chunks, checks incremental frame continuity, samples retained-window/segment state, and reports process and managed memory plus throughput. Repeated audio exercises runtime mechanics only; it is not a speaker-accuracy benchmark.

`tests/NemotronDiarizationSharp.RealSpeech` is an opt-in real-speech CPU pilot harness. It reads an external WAV and matching reference RTTM, crops both to the requested excerpt, streams mono float32 samples through the public SDK API, checks frame/segment/probability consistency, and writes prediction plus cropped-reference RTTM files and a JSON run report. It is not included in the normal test suite and does not implement DER scoring; use a trusted scorer with collar 0 and overlap enabled, or report DER as `NOT_MEASURED`. The harness accepts PCM 16/24/32-bit or IEEE float32 RIFF/WAVE audio at 8–96 kHz and downmixes multichannel input by averaging channels. It uses the `v3-streaming` 30.4-second geometry (`speaker cache=264`, `FIFO=40`, `chunk=340`, `right context=40`, `update period=300`), pushes one-second chunks sequentially, and attempts below-normal process priority.

Example preflight and run (keep all audio, RTTM and outputs outside the repository):

```powershell
$env:NEMOTONDIARIZATIONSHARP_MODEL = '<path-to-verified-model.gguf>'
dotnet run --project tests/NemotronDiarizationSharp.RealSpeech -c Release -- --audio '<meeting-mix.wav>' --reference-rttm '<meeting.rttm>' --start 300 --duration 180 --prediction-rttm '<prediction.rttm>' --cropped-reference-rttm '<reference-excerpt.rttm>' --report '<run.json>' --preflight
dotnet run --project tests/NemotronDiarizationSharp.RealSpeech -c Release -- --audio '<meeting-mix.wav>' --reference-rttm '<meeting.rttm>' --start 300 --duration 180 --prediction-rttm '<prediction.rttm>' --cropped-reference-rttm '<reference-excerpt.rttm>' --report '<run.json>'
```

In Windows PowerShell, set process-scoped paths and run a short preflight first:

```powershell
$env:NEMOTONDIARIZATIONSHARP_MODEL = '<path-to-verified-model.gguf>'
$env:NEMOTONDIARIZATIONSHARP_PCM_F32LE = '<path-to-verified-mono-f32le.raw>'
$env:NEMOTONDIARIZATIONSHARP_NATIVE_DIR = '<path-to-NeMo-Speech.cpp-v0.2.0-bin>'
dotnet run --project tests/NemotronDiarizationSharp.LongRun -c Release -- --duration-minutes 0.25
```

Then request at least 25 minutes of logical audio:

```powershell
dotnet run --project tests/NemotronDiarizationSharp.LongRun -c Release -- --duration-minutes 25 --require-compaction
```

Use `--chunk-seconds`, `--poll-seconds`, and `--diagnostic-interval-seconds` to change bounded chunk size, incremental polling cadence, and memory/snapshot sampling cadence. The harness defaults to 1-second pushes, 5-second incremental reads, snapshots every 60 logical seconds, and a 120-minute wall-clock limit. A short preflight without `--require-compaction` validates execution and cleanup without claiming compaction. A resource-pressure stop, incomplete target, frame gap, invalid output, or required-but-unobserved compaction is reported and does not count as compaction success. Each `ReadNewFrames()` still copies the complete retained native probability window; more frequent polling can therefore increase copying and allocation. Compare separate runs to assess polling cadence. Process working set/private bytes and managed allocation/heap estimates are observations, not a fixed leak threshold.

## Offline inference

`DiarizationModel.ProcessOffline(ReadOnlySpan<float>, int)` processes one complete mono float32 input as a stateless full-attention job. It returns a completed snapshot with probability history starting at frame zero. This is distinct from opening a streaming session, even when the model uses an offline streaming-geometry preset. Offline input is bounded by the model's positional capacity; longer recordings can fail with a native invalid-argument error. Use streaming for long recordings. Offline and streaming predictions are not guaranteed to be identical.

## Model and segmentation options

`DiarizationModel.Load(modelPath)` uses Auto backend selection and native default geometry. Its existing overload accepts `DiarizationModelOptions`. `Backend` can require CPU or CUDA; `GpuDeviceIndex` selects a CUDA device only after CUDA is selected, with null defaulting to device 0.

Use `DiarizationPresets` constants with the existing string `Preset` property:

| Constant | Native preset name |
| --- | --- |
| `DiarizationPresets.Streaming` | `streaming` |
| `DiarizationPresets.Offline` | `offline` |
| `DiarizationPresets.V3Streaming` | `v3-streaming` |
| `DiarizationPresets.V3Offline` | `v3-offline` |

Every preset configures stateful streaming. The offline names use larger streaming geometries. `ProcessOffline(samples, sampleRate, options)` separately runs stateless full-attention inference and is bounded by the model's positional capacity.

For the NVIDIA Nemotron 3 model, four additional managed profiles match the [official model-card table](https://huggingface.co/nvidia/Nemotron-3-Diarization). NVIDIA labels the first configuration “Very high latency (offline)”; `Nemotron3OfflineStyle` names its streaming geometry clearly. All values below are coarse encoder frames, each 80 ms. The latency is `(ChunkFrames + RightContextFrames) × 80 ms`.

| Managed profile | Official latency label | Speaker cache | FIFO | Chunk | Right context | Update period |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| `Nemotron3OfflineStyle` | Very high latency (offline), 30.4 s | 264 | 40 | 340 | 40 | 300 |
| `Nemotron3LowLatency` | Low latency, 1.04 s | 264 | 264 | 9 | 4 | 222 |
| `Nemotron3VeryLowLatency` | Very low latency, 0.64 s | 264 | 264 | 6 | 2 | 222 |
| `Nemotron3UltraLowLatency` | Ultra-low latency, 0.32 s | 264 | 264 | 3 | 1 | 222 |

Use them through the existing `Preset` property, for example `Preset = DiarizationPresets.Nemotron3LowLatency`. Each profile selects the supported native `v3-streaming` preset in NeMo-Speech.cpp v0.2.0 and supplies these geometry values as managed overrides; none exactly aliases a pinned native geometry preset. Left context remains the native V3 streaming default of 0 frames. The profiles target Nemotron-3 V3-compatible model files and a compatible v0.2.0 runtime; native model validation still applies. Existing geometry options can override profile values, including through the existing JSON `modelOptions` fields. Nonpositive values inherit profile values for all fields except `LeftContextFrames`, where zero is explicit and negative/null inherits. NVIDIA does not publish V3 segmentation-threshold recommendations in this table, so native segmentation defaults are unchanged.

All geometry values use coarse **80 ms encoder frames**, independent of probability output cadence; V3 native probabilities use 10 ms frames. Omitted/null geometry inherits the preset. For `ChunkFrames`, `RightContextFrames`, `FifoFrames`, `SpeakerCacheFrames`, and `UpdatePeriodFrames`, zero or negative also inherits. For `LeftContextFrames`, negative inherits and **zero is an explicit override**. Therefore, zero cannot explicitly disable right context or FIFO through overrides, though a preset itself can contain zero.

Before native allocation, managed `long` calculations reject effective geometry that would overflow signed native frame sums/products. Bounds derive from those expressions; there is no arbitrary geometry maximum. This safety proof covers the supported pinned `Nemotron-3-Diarization.q8_0.gguf` checkpoint (SHA-256 `08456d9e22cd9a323c0364d98375f3746d6e68507ebb705cd46438c534c7a3a1`) with NeMo-Speech.cpp v0.2.0. Encoder subsampling is 8; the supported V3 contract uses output subsampling 1 and upsampling 8, while the V2 defaults use output subsampling 8 and upsampling 1. Silence padding is 3. It does not cover arbitrary GGUF metadata. Native validation still checks preset names, minimum speaker-cache size, and model positional capacity.

With `using NemotronDiarizationSharp;`, construct options as follows; pass the result to `DiarizationModel.Load(modelPath, CreateStreamingOptions())`:

<!-- sdk-example: preset-options -->
```csharp
static DiarizationModelOptions CreateStreamingOptions()
{
    return new DiarizationModelOptions
    {
        GpuDeviceIndex = null,
        Preset = DiarizationPresets.V3Streaming,
        LeftContextFrames = 0,
        ChunkFrames = null
    };
}
```

### Optional JSON configuration

`DiarizationConfiguration.Parse(json)` uses framework-provided `System.Text.Json` and adds no package dependency. Parsing performs managed validation and does not load a model or run inference. The root must be an object with optional, exact case-sensitive camelCase sections `modelOptions` and `segmentationOptions`.

All existing model options are supported: `gpuDeviceIndex`, `preset`, `chunkFrames`, `rightContextFrames`, `leftContextFrames`, `fifoFrames`, `speakerCacheFrames`, and `updatePeriodFrames`. The Nemotron 3 profiles are values of the existing `preset` field, and the geometry fields can customize them. Segmentation supports `onset`, `offset`, `padOnsetSeconds`, `padOffsetSeconds`, `minGapSeconds`, and `minDurationSeconds`. Omitted/null scalar values preserve existing sentinels. Omitted/null `modelOptions` produces default model options; omitted/null `segmentationOptions` remains null, while `{}` creates explicit default segmentation options.

Unknown or duplicate fields (including equivalent escaped names), wrong case/types, comments, trailing commas/content, malformed JSON, and nonfinite or overflowing numbers are rejected with `JsonException`. Managed GPU/geometry/segmentation validation also reports invalid values as `JsonException`. Native preset and model-specific checks remain native.

The following functions require `using NemotronDiarizationSharp;`. Supply an external GGUF `modelPath`, staged compatible native runtime, finite mono float32 PCM `samples`, and `sampleRate` from 8,000 to 96,000 Hz. The application provides decoded audio. Only calling the processing functions starts native inference; the preset and JSON construction functions are managed. For longer recordings, push bounded chunks sequentially before finishing the stream.

<!-- sdk-example: json-configuration -->
```csharp
static DiarizationConfiguration CreateConfiguration()
{
    return DiarizationConfiguration.Parse("""
        {
          "modelOptions": {
            "gpuDeviceIndex": null,
            "preset": "v3-streaming",
            "leftContextFrames": 0,
            "chunkFrames": null
          },
          "segmentationOptions": {
            "onset": 0.64,
            "offset": 0.4,
            "padOnsetSeconds": null,
            "padOffsetSeconds": 0.1,
            "minGapSeconds": 0,
            "minDurationSeconds": null
          }
        }
        """);
}

static DiarizationSnapshot ProcessConfiguredStream(string modelPath, float[] samples, int sampleRate)
{
    DiarizationConfiguration configuration = CreateConfiguration();
    using var model = DiarizationModel.Load(modelPath, configuration.ModelOptions);
    using var stream = model.OpenStream();
    stream.Push(samples, sampleRate);
    stream.Finish();
    return stream.GetSnapshot(configuration.SegmentationOptions);
}

static DiarizationSnapshot ProcessConfiguredOffline(string modelPath, float[] samples, int sampleRate)
{
    DiarizationConfiguration configuration = CreateConfiguration();
    using var model = DiarizationModel.Load(modelPath, configuration.ModelOptions);
    return model.ProcessOffline(samples, sampleRate, configuration.SegmentationOptions);
}
```

## Opt-in CUDA smoke test

The optional tests/NemotronDiarizationSharp.CudaPilot diagnostic host requires separately supplied compatible CUDA runtime files, model and audio inputs. It performs preflight unless --run-smoke is specified. See its Program.cs for arguments. Keep inputs and generated output outside Git; a smoke run does not establish general GPU compatibility or redistribution rights.

Pass `DiarizationSegmentationOptions` to `DiarizationStream.GetSnapshot(options)` or `DiarizationModel.ProcessOffline(samples, sampleRate, options)` to configure segment postprocessing. Onset and offset are speech-entry and speech-exit hysteresis thresholds; padding widens segment boundaries, minimum gap fills short gaps, and minimum duration removes short segments. Omitted values are sent as native defaults. The native ABI also treats zero or negative values as defaults, so they cannot request explicit zero or negative overrides. Calls without options pass a null configuration and preserve native default behavior.

Native streaming compacts probability history only when it finds a suitable interval where all speakers are silent. Compaction is not guaranteed at a fixed recording duration and does not impose a hard native-memory limit. Finalized historical segments remain available, but custom segmentation settings apply only to the retained tail; older segments may already reflect native defaults. Changing settings cannot retroactively reprocess compacted history. Native compaction is independent of managed `DiarizationProbabilityHistory` retention limits.

## Incremental frame consumption

Call `DiarizationStream.ReadNewFrames()` to receive a `DiarizationFrameBatch` containing only frames not returned by earlier incremental reads on that stream. Each batch has an absolute `FirstFrameIndex`, a frame count, speaker count, cadence, read-only frame-major probabilities, and `GetProbability(absoluteFrameIndex, speakerIndex)`. Empty reads do not move the cursor. `GetSnapshot()` remains independent and does not consume frames; `Finish()` makes any final tail available to the next read. Batches own their values and remain usable after stream disposal.

If native history compaction advances past unread frames, `ReadNewFrames()` throws `DiarizationFrameHistoryGapException` with the missing range and earliest retained frame. Poll often enough to avoid gaps; finalized segments are a separate result and cannot restore compacted frame probabilities. The native v0.2.0 API copies the entire retained probability window on every read, even when the returned batch contains only a small tail. A batch captures values as returned by that read; the native ABI does not promise that previously published probabilities are immutable.

## Asynchronous PCM input

`await stream.PushChunksAsync(chunks, sampleRate, cancellationToken)` consumes an `IAsyncEnumerable<ReadOnlyMemory<float>>` sequentially: it waits for one chunk, pushes it, then asks the source for the next. It uses no recording-sized buffer or producer queue. Native `Push` remains synchronous and runs on a worker thread; cancellation cannot interrupt an in-flight native call. The source receives the token while enumerating, so a source that ignores cancellation may delay cancellation until its next chunk. A chunk already yielded is fully pushed before cancellation is reported. No `Finish()` is called automatically.

Do not modify or reuse yielded memory until the returned task completes. On cancellation, completed chunks remain in the open stream, which can be resumed or explicitly finished. Source and input-validation errors also leave the successfully pushed prefix usable; a native push failure makes the stream state uncertain, so only disposal is allowed. While async processing is active, synchronous pushes, finish, snapshots, incremental reads, disposal, and another async operation are rejected. Await the operation before using or disposing this stream. The model may be disposed while its stream is active; the stream's safe handle retains the native model until the stream closes.

## Design constraints

- Target .NET 10 (`net10.0`); locally validated native runtime targets are Windows x64 CPU and CUDA; broader compatibility remains unverified.
- Keep model weights outside NuGet packages.
- Preserve speaker probabilities, frame timing, segments, streaming behavior, and explicit disposal.
- Do not assume native dependencies can be redistributed until their licenses are reviewed.
- Python, PyTorch, and FFmpeg are not required by the managed runtime.

## Revision-aware results and managed postprocessing

`ReadNewFrames()` is an append-only cursor: it returns only frames not returned by earlier calls. `ReadResultUpdates()` has a separate cursor and compares the current retained probability window with its prior observation. Each owned update contains `NewFrames`, zero or more changed `RevisedFrames`, and `Segments`, the complete latest segment list that replaces the previous list. `SourceId` and consecutive `Sequence` values identify this managed cursor; they are not native stream IDs. `FirstRetainedFrame`, `FrameCount`, `ComparedFrames`, and `IsFinal` describe the observed window and its coverage. Snapshot reads do not advance either cursor.

If compaction removed unread frames, the default read throws `DiarizationFrameHistoryGapException` and leaves the cursor available for recovery. An explicit `ReadResultUpdates(allowHistoryGap: true)` reports the missing range in `HistoryGap` and returns the available tail. Treat that update as incomplete: do not apply it to a history that claims contiguous coverage from before the gap. The native ABI has no stable-frame watermark, so a later update can revise earlier observed probabilities. Non-final segment lists are provisional and should be replaced by each update's latest list. Compaction can hide a final revision. Managed scratch/cache buffers are reused after warm-up, but the native ABI still copies and compares the whole retained window; segment snapshots and cache high-water memory are additional costs.

### Synthetic postprocessing and probability history

`DiarizationPostprocessingOptions` is a concrete managed configuration whose numeric zero values are explicit. Defaults are onset/offset `0.5` and zero padding, minimum gap, and minimum duration. Equality with the onset threshold does not start speech; equality with the offset threshold does not end it. The managed algorithm detects speech, pads and clips it to the supplied frame window, merges same-speaker segments when the gap is less than or equal to `MinGapSeconds`, then drops segments shorter than `MinDurationSeconds`. Speakers are processed independently, so overlaps remain. This is a separate managed algorithm and does not promise the same output as native segmentation. The existing `DiarizationSegmentationOptions` and JSON configuration retain their native sentinel behavior.

`DiarizationProbabilityHistory` requires either a positive `maxRetainedFrames` limit or explicit `null` for full in-memory retention. `Apply` consumes consecutive updates from one source, applies revisions, and appends new frames atomically; it rejects reported gaps and discontinuities. Deliberate eviction is visible through `FirstRetainedFrame` and `IsCompleteFromFrameZero`, which describes frame coverage, not revision stability. `GetFrames()` returns an owned copy. Before binding to live updates, `Append` can restore externally persisted `DiarizationFrameBatch` values, provided the batches are contiguous and share metadata. The first live update must continue exactly at the restored end. Persistence and native stream resumption are application responsibilities. Bounded-history eviction removes earlier captured probabilities from that history. Native compaction removes uncaptured native values and can hide later revisions, but values already captured in managed history remain available according to that history's retention policy.

The synthetic example below uses no model or native runtime. RTTM output goes to a caller-owned `TextWriter`; recording IDs must have no whitespace, channels must be positive, and speaker numbers are one-based. Overlapping speakers are preserved. Invalid input or a writer/enumeration failure can leave partial output, so this helper does not claim atomic persistence.

<!-- sdk-example: synthetic-postprocessing -->
```csharp
static string CreateSyntheticRttm()
{
    var history = new DiarizationProbabilityHistory(maxRetainedFrames: null);
    history.Append(DiarizationFrameBatch.FromProbabilities(0, 1, 1.0, [0.8f, 0.1f]));
    history.Append(DiarizationFrameBatch.FromProbabilities(2, 1, 1.0, [0.8f]));

    var options = new DiarizationPostprocessingOptions
    {
        Onset = 0.5f,
        Offset = 0.5f,
        PadOffsetSeconds = 0.5,
        MinGapSeconds = 0.5,
        MinDurationSeconds = 0
    };
    IReadOnlyList<DiarizationSegment> segments = DiarizationPostprocessor.Segment(history.GetFrames(), options);
    using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
    DiarizationPostprocessor.WriteRttm(writer, segments, recordingId: "synthetic");
    return writer.ToString();
}
```

### Async streaming results

`PushChunksAsync()` consumes PCM sequentially and leaves `Finish()` to the caller. `ProcessStreamAsync()` instead yields one result update after each acquired chunk, including empty chunks, then calls `Finish()` and reads one final update when the source completes normally. It is pull-based: the next source chunk is not requested until the consumer asks for the next update. The iterator reserves the stream through its final yield or disposal. The source receives the cancellation token. Cancellation while waiting is cooperative, and an already acquired chunk completes its native push and result read before cancellation is observed. Native calls cannot be interrupted. Cancellation, early loop exit, source failure, or input validation failure do not call `Finish()`. After a post-push read error the PCM is already committed, so retry result reading rather than replaying that chunk. If `Finish()` succeeds but the final result read fails, the stream remains finished; `ReadResultUpdates()` can retry the read, but `ProcessStreamAsync()` cannot be restarted. Read/gap failures are recoverable; native `Push` or `Finish` failures require disposing the stream after iterator cleanup.

This compiled example expects an existing stream and application-decoded mono float32 PCM chunks at one explicit, consistent sample rate. Its illustrative 12,000-frame limit is model-cadence dependent and is not a cap on total native or process memory. It returns segments for the currently retained window; it does not buffer the recording or perform decoding/resampling.

<!-- sdk-example: async-results -->
```csharp
static async Task<IReadOnlyList<DiarizationSegment>> ProcessAsyncResults(
    DiarizationStream stream,
    IAsyncEnumerable<ReadOnlyMemory<float>> monoPcmChunks,
    int sampleRate,
    CancellationToken cancellationToken)
{
    var history = new DiarizationProbabilityHistory(maxRetainedFrames: 12_000);
    await foreach (DiarizationResultUpdate update in
        stream.ProcessStreamAsync(monoPcmChunks, sampleRate, cancellationToken))
    {
        history.Apply(update);
        IReadOnlyList<DiarizationSegment> provisionalSegments = DiarizationPostprocessor.Segment(
            history.GetFrames(), new DiarizationPostprocessingOptions());
        if (update.IsFinal)
            return provisionalSegments;
    }

    return Array.Empty<DiarizationSegment>();
}
```

For gap-aware polling and latest segment replacement, see the revision-polling code example below. The examples are compiled by managed tests; only the synthetic construction, restoration, segmentation, and RTTM example is executed there. They do not exercise native inference or real native cancellation/revision behavior. In an application, include `using NemotronDiarizationSharp;` and the standard `System` namespaces used by the signatures; the snippets show methods from the normally compiled test file.

<!-- sdk-example: revision-polling -->
```csharp
static (DiarizationResultUpdate Update, bool IsIncomplete) PollRevisionAwareUpdate(DiarizationStream stream)
{
    try
    {
        return (stream.ReadResultUpdates(), false);
    }
    catch (DiarizationFrameHistoryGapException)
    {
        DiarizationResultUpdate recovered = stream.ReadResultUpdates(allowHistoryGap: true);
        return (recovered, recovered.HistoryGap.HasValue);
    }
}

static void DisplayLatestResult(DiarizationStream stream)
{
    (DiarizationResultUpdate update, bool incomplete) = PollRevisionAwareUpdate(stream);
    Console.WriteLine($"Update {update.Sequence}, frames [{update.FirstRetainedFrame}, {update.FrameCount}), final={update.IsFinal}");
    if (update.HistoryGap is { } gap)
        Console.WriteLine($"Missing frames [{gap.StartFrame}, {gap.EndFrameExclusive})");
    foreach (DiarizationFrameBatch revision in update.RevisedFrames)
        Console.WriteLine($"Revised [{revision.FirstFrameIndex}, {revision.FirstFrameIndex + revision.FrameCount})");
    Console.WriteLine(incomplete ? "Probability history is incomplete after a reported gap." : "No unread history gap.");
    IReadOnlyList<DiarizationSegment> latestSegments = update.Segments;
    Console.WriteLine($"Latest replacement segment count: {latestSegments.Count}");
}
```

