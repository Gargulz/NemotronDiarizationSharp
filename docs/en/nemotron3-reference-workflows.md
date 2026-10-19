# Nemotron 3 reference workflows

The SDK keeps model geometry and segment extraction as independent choices. The completed-recording and live entry points select NVIDIA's published Nemotron 3 geometry and NeMo-compatible timestamp processing by default. This is an SDK workflow choice based on the official model card; it is not a claim that NVIDIA recommends one universal postprocessing configuration.

The repository's [compiled example methods](../../examples/NemotronDiarizationSharp.Console/Nemotron3ReferenceExamples.cs) cover WAV, decoded PCM, live updates, overrides, native and managed modes, and RTTM export.

## Completed WAV recording

```csharp
using NemotronDiarizationSharp;

using var model = DiarizationModel.LoadForRecording("nemotron3.gguf");
DiarizationSnapshot result = model.ProcessWaveFile("meeting.wav");

Console.WriteLine($"Final: {result.IsFinal}; frames: {result.FrameCount}");
DiarizationPostprocessor.WriteRttm(Console.Out, result.Segments, "meeting");
```

`LoadForRecording` selects the official offline-style **streaming** geometry: 264 speaker-cache frames, 40 FIFO frames, 340 chunk frames, 40 right-context frames, and update period 300. `ProcessWaveFile` decodes bounded PCM chunks and pushes them through a stream, retaining and revising the probability history until finalization. It does not load the full decoded waveform into memory. Reference recomputation requires probability history from frame zero; a native history gap causes an error rather than a partial final result.

The lightweight WAV reader accepts little-endian RIFF/WAVE with PCM 8/16/24/32-bit or IEEE float32 samples, 8–96 kHz, and averages channels to mono. It rejects WAVE_FORMAT_EXTENSIBLE, RIFX, RF64, and other codecs. Other formats must be decoded by the application and supplied as mono float32 PCM.

## Completed decoded PCM

For short recordings, true full-attention inference is available as an advanced operation:

```csharp
using var model = DiarizationModel.LoadForRecording("nemotron3.gguf");
DiarizationSnapshot result = model.ProcessOfflineWithResults(
    monoFloat32Pcm, sampleRate: 16_000,
    new DiarizationResultOptions
    {
        SegmentationMode = DiarizationSegmentationMode.NvidiaNeMoReference
    });
```

This uses stateless full-attention inference and is subject to the model's positional capacity. For long PCM inputs, push bounded chunks into `OpenStream()`, apply each `ReadResultUpdates()` update to an unbounded `DiarizationProbabilityHistory(null)`, call `Finish()`, and read the final update before segmenting `history.GetFrames()`. A reported history gap must be treated as incomplete input.

## Live or simulated streaming

```csharp
await Nemotron3ReferenceExamples.RunLiveWaveAsync(
    "nemotron3.gguf", "microphone-capture.wav",
    update => Console.WriteLine($"Final={update.IsFinal}, segments={update.Segments.Count}"),
    cancellationToken);
```

The live geometry uses the published 1.04-second configuration: 264 speaker-cache frames, 264 FIFO frames, 9 chunk frames, 4 right-context frames, and update period 222. Updates may revise probabilities and segments; provisional segments are not immutable. A stream `GetSnapshot()` in reference mode reports its retained absolute frame bounds. After native compaction, the snapshot covers only retained frames. A final reference result that has lost frames before frame zero is rejected.

## Geometry and segmentation overrides

Geometry can be changed without changing the segmentation mode:

```csharp
using var model = DiarizationModel.LoadForStreaming("nemotron3.gguf",
    new DiarizationModelOptions { ChunkFrames = 12, RightContextFrames = 5 });
```

Reference settings can be supplied independently:

```csharp
var reference = new DiarizationResultOptions
{
    SegmentationMode = DiarizationSegmentationMode.NvidiaNeMoReference,
    NeMoReferenceOptions = new DiarizationPostprocessingOptions
    {
        Onset = 0.55f,
        Offset = 0.45f,
        PadOnsetSeconds = 0.02,
        PadOffsetSeconds = 0.02,
        MinGapSeconds = 0.10,
        MinDurationSeconds = 0.08
    }
};
DiarizationSnapshot customReference = model.ProcessWaveFile("meeting.wav", reference);
```

Choose the original native CALLHOME-oriented segmentation directly:

```csharp
DiarizationSnapshot nativeResult =
    Nemotron3ReferenceExamples.RunNativeWave("nemotron3.gguf", "meeting.wav");
```

Choose the project's separate managed hysteresis algorithm, or process owned raw probabilities yourself:

```csharp
IReadOnlyList<DiarizationSegment> managedSegments =
    Nemotron3ReferenceExamples.RunApplicationPostprocessor(probabilityBatch,
        new DiarizationPostprocessingOptions { Onset = 0.6f, Offset = 0.4f });
```

`DiarizationSnapshot.GetProbability` and `ToFrameBatch()` expose the owned raw probabilities independently of the segment list. The existing `DiarizationPostprocessor.WriteRttm` writes positive-duration, one-based SDK speaker IDs. NeMo labels its internal channels `speaker_0`, `speaker_1`, …; the SDK maps them to its public one-based `Speaker` values.

```csharp
Nemotron3ReferenceExamples.ExportRttm(Console.Out, "meeting", result);
```

## Reference source and exact behavior

The selected primary reference is NVIDIA NeMo Speech **v3.0.0**, commit [`fd6a877539710e2b98f28c43272ff81312f83417`](https://github.com/NVIDIA-NeMo/Speech/commit/fd6a877539710e2b98f28c43272ff81312f83417), specifically `SortformerEncLabelModel._diarize_output_processing` and `ts_vad_post_processing` in [`sortformer_diar_models.py`](https://github.com/NVIDIA-NeMo/Speech/blob/fd6a877539710e2b98f28c43272ff81312f83417/nemo/collections/asr/models/sortformer_diar_models.py) and [`vad_utils.py`](https://github.com/NVIDIA-NeMo/Speech/blob/fd6a877539710e2b98f28c43272ff81312f83417/nemo/collections/asr/parts/utils/vad_utils.py). The NVIDIA [Nemotron 3 model card](https://huggingface.co/nvidia/Nemotron-3-Diarization) documents the model and its offline-style and low-latency geometry profiles.

With no postprocessing YAML, the NeMo mixin supplies onset/offset 0.5, zero pads, and zero minimum speech/silence durations. `ts_vad_post_processing` repeats each model probability by the encoder subsampling factor, then binarizes and filters. Binarization opens only when probability is strictly greater than onset; an active run closes only when probability is strictly below offset, so equality preserves the prior state. Padding is applied before overlap merging. Filtering removes short speech first, then merges gaps strictly shorter than the silence threshold. The final active run ends at the index of its last 10 ms frame (rather than the exclusive frame boundary). The Sortformer path adds the recording offset and rounds timestamps to two decimal places. Each speaker channel is processed independently, so overlap is preserved.

The SDK adaptation maps native probability cadence to NeMo's 10 ms units and public speaker indexes to one-based IDs. Model-free tests cover the source-derived boundary cases; they do not execute NVIDIA's Python implementation. NeMo's helper is marked TODO in the pinned Sortformer source as not covered by the mixin test, so this is pinned-source compatibility, not independently executed cross-runtime parity. Hugging Face Transformers uses a different postprocessing path and is not substituted here. NeMo-Speech.cpp native segmentation is another distinct path and remains directly selectable; its CALLHOME-tuned defaults are not used by these profile entry points.

## Offline-style streaming and full-attention offline inference

“Offline-style” is a long-context streaming geometry, not stateless full-attention inference. `LoadForRecording` uses that geometry while `ProcessWaveFile` feeds chunks through a stream. `ProcessOffline` and `ProcessOfflineWithResults` are the separate full-attention APIs and remain limited by positional capacity.

The GGUF model and native runtime are adaptations; this SDK does not claim numerical equivalence to NVIDIA's original checkpoint, verified CUDA execution, or identical runtime internals.
