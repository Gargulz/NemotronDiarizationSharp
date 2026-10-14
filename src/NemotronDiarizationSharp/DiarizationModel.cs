using System.Runtime.InteropServices;
using NemotronDiarizationSharp.Native;

namespace NemotronDiarizationSharp;

public sealed class DiarizationModel : IDisposable
{
    private readonly ModelHandle _handle;
    private readonly DiarizationSegmentationMode _defaultSegmentationMode;
    private readonly object _sync = new();
    private bool _disposed;

    private DiarizationModel(ModelHandle handle, DiarizationSegmentationMode defaultSegmentationMode)
    {
        _handle = handle;
        _defaultSegmentationMode = defaultSegmentationMode;
        try
        {
            int speakerCount = NativeRuntimeCompatibility.InvokeRequiredEntryPoint(
                "nemo_speech_diar_num_speakers", () => NativeMethods.DiarNumSpeakers(handle));
            double secondsPerFrame = NativeRuntimeCompatibility.InvokeRequiredEntryPoint(
                "nemo_speech_diar_seconds_per_frame", () => NativeMethods.DiarSecondsPerFrame(handle));
            ValidateNativeMetadata(speakerCount, secondsPerFrame);

            SpeakerCount = speakerCount;
            SecondsPerFrame = secondsPerFrame;
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    internal static void ValidateNativeMetadata(int speakerCount, double secondsPerFrame)
    {
        if (speakerCount <= 0 || speakerCount > Array.MaxLength)
            throw new InvalidOperationException($"The native library returned an invalid speaker count: {speakerCount}.");
        if (!double.IsFinite(secondsPerFrame) || secondsPerFrame <= 0)
            throw new InvalidOperationException($"The native library returned an invalid frame duration: {secondsPerFrame}.");
    }

    public int SpeakerCount { get; }
    public double SecondsPerFrame { get; }
    public static string NativeRuntimeVersion => NativeMethods.GetVersion();

    public static DiarizationModel Load(string modelPath)
        => LoadCore(modelPath, options: null, DiarizationSegmentationMode.Native);

    /// <summary>Loads a model using the requested CPU streaming geometry.</summary>
    public static DiarizationModel Load(string modelPath, DiarizationModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return LoadCore(modelPath, options, DiarizationSegmentationMode.Native);
    }

    /// <summary>Loads a model with the published Nemotron 3 offline-style streaming geometry for a completed recording.</summary>
    /// <remarks>This remains chunked streaming inference; use <see cref="ProcessOffline(ReadOnlySpan{float}, int)"/> for full-attention inference.</remarks>
    public static DiarizationModel LoadForRecording(string modelPath, DiarizationModelOptions? overrides = null)
        => LoadCore(modelPath, (overrides ?? new DiarizationModelOptions())
            .WithDefaultPreset(DiarizationPresets.Nemotron3OfflineStyle), DiarizationSegmentationMode.NvidiaNeMoReference);

    /// <summary>Loads a model with the published 1.04-second Nemotron 3 low-latency geometry for live streaming.</summary>
    public static DiarizationModel LoadForStreaming(string modelPath, DiarizationModelOptions? overrides = null)
        => LoadCore(modelPath, (overrides ?? new DiarizationModelOptions())
            .WithDefaultPreset(DiarizationPresets.Nemotron3LowLatency), DiarizationSegmentationMode.NvidiaNeMoReference);

    private static DiarizationModel LoadCore(string modelPath, DiarizationModelOptions? options,
        DiarizationSegmentationMode defaultSegmentationMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        int gpuDeviceIndex = options?.GetNativeGpuDeviceIndex() ?? -1;
        options?.ValidateGeometryArithmetic();
        string fullPath = Path.GetFullPath(modelPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The diarization model file was not found.", fullPath);

        nint utf8Path = 0;
        nint utf8Preset = 0;
        try
        {
            utf8Path = Marshal.StringToCoTaskMemUTF8(fullPath);
            string? nativePreset = options?.GetNativePreset();
            if (nativePreset is not null)
                utf8Preset = Marshal.StringToCoTaskMemUTF8(nativePreset);

            var config = new DiarModelConfig
            {
                Size = (nuint)Marshal.SizeOf<DiarModelConfig>(),
                ModelPath = utf8Path,
                Gpu = gpuDeviceIndex,
                Preset = utf8Preset,
                LeftContextFrames = -1
            };
            if (options is not null)
                options.ApplyNativeOverrides(ref config);

            NativeRuntimeCompatibility.EnsureCompatible();

            (NativeStatus status, nint model) = NativeRuntimeCompatibility.InvokeRequiredEntryPoint(
                "nemo_speech_diar_create", () =>
                {
                    NativeStatus createStatus = NativeMethods.DiarCreate(in config, out nint createdModel);
                    return (createStatus, createdModel);
                });

            if (status != NativeStatus.Ok)
            {
                NativeCallException exception = NativeCallException.Create(status);
                if (model != 0)
                    NativeMethods.DiarDestroy(model);
                throw exception;
            }

            if (model == 0)
                throw new InvalidOperationException("The native library returned a null model handle.");

            return new DiarizationModel(new ModelHandle(model), defaultSegmentationMode);
        }
        finally
        {
            if (utf8Preset != 0)
                Marshal.FreeCoTaskMem(utf8Preset);
            if (utf8Path != 0)
                Marshal.FreeCoTaskMem(utf8Path);
        }
    }

    public DiarizationStream OpenStream()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return OpenStreamCore();
        }
    }

    private DiarizationStream OpenStreamCore()
    {
        bool modelReferenceAdded = false;
        nint stream = 0;
        try
        {
            // Резервируем модель до открытия native stream: Dispose может идти с другого потока.
            _handle.DangerousAddRef(ref modelReferenceAdded);
            NativeStatus status = NativeMethods.DiarStreamOpen(_handle, out stream);
            if (status != NativeStatus.Ok)
                throw NativeCallException.Create(status);
            if (stream == 0)
                throw new InvalidOperationException("The native library returned a null stream handle.");

            var streamHandle = new StreamHandle(stream, _handle);
            stream = 0;
            modelReferenceAdded = false;
            return new DiarizationStream(streamHandle, SpeakerCount, SecondsPerFrame, _defaultSegmentationMode);
        }
        finally
        {
            if (modelReferenceAdded)
            {
                ClosePartialStreamAndReleaseModel(stream, NativeMethods.DiarStreamClose, _handle.DangerousRelease);
                modelReferenceAdded = false;
            }
        }
    }

    internal static void ClosePartialStreamAndReleaseModel(
        nint stream, Action<nint> closeStream, Action releaseModelReference)
    {
        ArgumentNullException.ThrowIfNull(closeStream);
        ArgumentNullException.ThrowIfNull(releaseModelReference);

        if (stream != 0)
            closeStream(stream);

        releaseModelReference();
    }

    /// <summary>
    /// Processes mono float32 PCM as one stateless full-attention job. Finite samples are
    /// passed unchanged; unlike streaming, offline processing is limited by the model's
    /// positional capacity.
    /// </summary>
    /// <param name="samples">Mono float32 PCM samples, expected to be normalized to [-1, 1].</param>
    /// <param name="sampleRate">Input rate from 8000 to 96000 Hz; the native runtime resamples to the model rate.</param>
    /// <returns>A completed snapshot with probability history beginning at frame zero.</returns>
    public DiarizationSnapshot ProcessOffline(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (_defaultSegmentationMode != DiarizationSegmentationMode.Native)
            return ProcessOfflineWithResults(samples, sampleRate,
                new DiarizationResultOptions { SegmentationMode = _defaultSegmentationMode });
        return ProcessOffline(samples, sampleRate, segmentationOptions: null);
    }

    /// <summary>Processes one complete input and selects native, managed, or NVIDIA NeMo reference segmentation.</summary>
    public DiarizationSnapshot ProcessOfflineWithResults(ReadOnlySpan<float> samples, int sampleRate,
        DiarizationResultOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        DiarizationSnapshot snapshot = ProcessOffline(samples, sampleRate,
            options.SegmentationMode == DiarizationSegmentationMode.Native ? options.NativeOptions : null);
        return DiarizationStream.ApplyResultOptions(snapshot, options);
    }

    /// <summary>Processes a WAV recording incrementally without loading the decoded audio into one array.</summary>
    /// <param name="wavePath">Path to little-endian RIFF/WAVE PCM 8/16/24/32-bit or IEEE float32 audio.</param>
    /// <param name="resultOptions">Optional independent segmentation selection.</param>
    /// <param name="chunkDurationMilliseconds">Decoded audio chunk size; defaults to 250 ms.</param>
    /// <remarks>
    /// Completed recording profiles recompute reference or managed segments from all revised probabilities.
    /// This retains the probability history in managed memory. Native mode returns the native stream result.
    /// </remarks>
    public DiarizationSnapshot ProcessWaveFile(string wavePath, DiarizationResultOptions? resultOptions = null,
        int chunkDurationMilliseconds = 250)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavePath);
        if (chunkDurationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkDurationMilliseconds));
        DiarizationResultOptions selected = resultOptions ??
            new DiarizationResultOptions { SegmentationMode = _defaultSegmentationMode };
        selected.Validate();

        using var reader = new DiarizationWaveReader(wavePath);
        int bufferSize = Math.Max(1, checked(reader.SampleRate * chunkDurationMilliseconds / 1000));
        var pcm = new float[bufferSize];
        using DiarizationStream stream = OpenStream();
        DiarizationProbabilityHistory? history = selected.SegmentationMode == DiarizationSegmentationMode.Native
            ? null
            : new DiarizationProbabilityHistory(maxRetainedFrames: null);
        while (true)
        {
            int frames = reader.Read(pcm);
            if (frames == 0)
                break;
            stream.Push(pcm.AsSpan(0, frames), reader.SampleRate);
            history?.Apply(stream.ReadResultUpdates());
        }

        stream.Finish();
        if (history is null)
            return stream.GetSnapshot(selected.NativeOptions);
        history.Apply(stream.ReadResultUpdates());
        if (!history.IsCompleteFromFrameZero || history.EndFrameExclusive == 0)
            throw new InvalidOperationException("The complete recording probability history is unavailable.");

        DiarizationFrameBatch framesBatch = history.GetFrames();
        var snapshot = new DiarizationSnapshot(0, framesBatch.FrameCount, framesBatch.SpeakerCount,
            framesBatch.SecondsPerFrame, framesBatch.Probabilities.ToArray(), [], isFinal: true);
        return DiarizationStream.ApplyResultOptions(snapshot, selected);
    }

    /// <summary>Processes one complete input and extracts segments using the supplied settings.</summary>
    public DiarizationSnapshot ProcessOffline(ReadOnlySpan<float> samples, int sampleRate,
        DiarizationSegmentationOptions? segmentationOptions)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DiarizationStream.ValidateOfflineInput(samples, sampleRate);
            NativeSegmentationConfig? nativeSegmentation = segmentationOptions?.ToNativeConfig();
            float[] pcm = samples.ToArray();
            bool modelReferenceAdded = false;
            nint result = 0;
            try
            {
                // Удерживаем модель, пока нативный результат не будет закрыт.
                _handle.DangerousAddRef(ref modelReferenceAdded);
                NativeStatus status = NativeMethods.DiarOfflineF32(_handle, pcm, (nuint)pcm.Length, sampleRate, out result);
                if (status != NativeStatus.Ok)
                    throw NativeCallException.Create(status);
                if (result == 0)
                    throw new InvalidOperationException("The native library returned a null offline result handle.");

                var job = new StreamHandle(result, _handle);
                result = 0;
                modelReferenceAdded = false;
                try
                {
                    return DiarizationStream.ReadSnapshot(job, SpeakerCount, SecondsPerFrame,
                        isFinal: true, segmentationConfig: nativeSegmentation);
                }
                finally
                {
                    job.Dispose();
                }
            }
            finally
            {
                bool resultClosed = result == 0;
                try
                {
                    if (result != 0)
                    {
                        NativeMethods.DiarStreamClose(result);
                        resultClosed = true;
                    }
                }
                finally
                {
                    if (modelReferenceAdded && resultClosed)
                        _handle.DangerousRelease();
                    // При сбое закрытия нативного результата удерживаем модель, чтобы не нарушить её время жизни.
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _handle.Dispose();
    }
}
