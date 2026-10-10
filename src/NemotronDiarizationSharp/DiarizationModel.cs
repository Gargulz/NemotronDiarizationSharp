using System.Runtime.InteropServices;
using NemotronDiarizationSharp.Native;

namespace NemotronDiarizationSharp;

public sealed class DiarizationModel : IDisposable
{
    private readonly ModelHandle _handle;
    private readonly object _sync = new();
    private bool _disposed;

    private DiarizationModel(ModelHandle handle)
    {
        _handle = handle;
        try
        {
            int speakerCount = NativeMethods.DiarNumSpeakers(handle);
            double secondsPerFrame = NativeMethods.DiarSecondsPerFrame(handle);
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
        => LoadCore(modelPath, options: null);

    /// <summary>Loads a model using the requested CPU streaming geometry.</summary>
    public static DiarizationModel Load(string modelPath, DiarizationModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return LoadCore(modelPath, options);
    }

    private static DiarizationModel LoadCore(string modelPath, DiarizationModelOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        string fullPath = Path.GetFullPath(modelPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The diarization model file was not found.", fullPath);

        nint utf8Path = 0;
        nint utf8Preset = 0;
        try
        {
            utf8Path = Marshal.StringToCoTaskMemUTF8(fullPath);
            if (options?.Preset is not null)
                utf8Preset = Marshal.StringToCoTaskMemUTF8(options.Preset);

            var config = new DiarModelConfig
            {
                Size = (nuint)Marshal.SizeOf<DiarModelConfig>(),
                ModelPath = utf8Path,
                Gpu = -1,
                Preset = utf8Preset,
                LeftContextFrames = -1
            };
            if (options is not null)
                options.ApplyNativeOverrides(ref config);

            NativeStatus status = NativeMethods.DiarCreate(in config, out nint model);
            if (status != NativeStatus.Ok)
            {
                NativeCallException exception = NativeCallException.Create(status);
                if (model != 0)
                    NativeMethods.DiarDestroy(model);
                throw exception;
            }

            if (model == 0)
                throw new InvalidOperationException("The native library returned a null model handle.");

            return new DiarizationModel(new ModelHandle(model));
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
            return new DiarizationStream(streamHandle, SpeakerCount, SecondsPerFrame);
        }
        finally
        {
            try
            {
                if (stream != 0)
                    NativeMethods.DiarStreamClose(stream);
            }
            finally
            {
                if (modelReferenceAdded)
                    _handle.DangerousRelease();
            }
        }
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
        => ProcessOffline(samples, sampleRate, segmentationOptions: null);

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

public sealed class DiarizationStream : IDisposable
{
    private readonly StreamHandle _handle;
    private readonly object _sync = new();
    private readonly int _speakerCount;
    private readonly double _secondsPerFrame;
    private bool _finished;
    private int? _sampleRate;
    private readonly IncrementalFrameCursor _frameCursor = new();

    internal DiarizationStream(StreamHandle handle, int speakerCount, double secondsPerFrame)
    {
        _handle = handle;
        _speakerCount = speakerCount;
        _secondsPerFrame = secondsPerFrame;
    }

    /// <summary>
    /// Appends mono float32 PCM. Samples are expected to be normalized to [-1, 1]; finite values
    /// are passed unchanged, without clipping or normalization. The explicit sample rate must be
    /// between 8000 and 96000 Hz and must remain constant after the first non-empty push. Empty
    /// pushes do not select a rate; after one is selected, they must specify the same rate. Unlike
    /// the native API, 0 is not a default-rate sentinel.
    /// </summary>
    public void Push(ReadOnlySpan<float> monoPcm, int sampleRate)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            if (_finished)
                throw new InvalidOperationException("The stream has already been finished.");
            ValidatePushInput(monoPcm, sampleRate, _sampleRate);
            if (monoPcm.IsEmpty)
                return;

            float[] samples = monoPcm.ToArray();
            NativeStatus status = NativeMethods.DiarStreamPushF32(_handle, samples, (nuint)samples.Length, sampleRate);
            if (status != NativeStatus.Ok)
                throw NativeCallException.Create(status);
            _sampleRate ??= sampleRate;
        }
    }

    internal static void ValidatePushInput(ReadOnlySpan<float> monoPcm, int sampleRate, int? currentSampleRate)
    {
        if (sampleRate is < 8000 or > 96000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be between 8 kHz and 96 kHz.");
        if (currentSampleRate.HasValue && currentSampleRate.Value != sampleRate)
            throw new ArgumentException("The sample rate cannot change within a stream.", nameof(sampleRate));

        foreach (float sample in monoPcm)
        {
            if (!float.IsFinite(sample))
                throw new ArgumentException("PCM samples must be finite float32 values.", nameof(monoPcm));
        }
    }

    internal static void ValidateOfflineInput(ReadOnlySpan<float> monoPcm, int sampleRate)
    {
        if (sampleRate is < 8000 or > 96000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be between 8 kHz and 96 kHz.");

        foreach (float sample in monoPcm)
        {
            if (!float.IsFinite(sample))
                throw new ArgumentException("PCM samples must be finite float32 values.", nameof(monoPcm));
        }
    }

    public void Finish()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            if (_finished)
                return;
            NativeStatus status = NativeMethods.DiarStreamFinish(_handle);
            if (status != NativeStatus.Ok)
                throw NativeCallException.Create(status);
            _finished = true;
        }
    }

    public DiarizationSnapshot GetSnapshot()
        => GetSnapshot(segmentationOptions: null);

    /// <summary>Reads a snapshot, applying segmentation settings to the native result.</summary>
    public DiarizationSnapshot GetSnapshot(DiarizationSegmentationOptions? segmentationOptions)
    {
        lock (_sync)
        {
            return GetSnapshotCore(segmentationOptions);
        }
    }

    /// <summary>Reads newly available frame probabilities without advancing snapshot reads.</summary>
    /// <remarks>
    /// Native v0.2.0 copies the complete retained probability window for each read. Read often
    /// enough to avoid native history compaction; returned batches own their managed values.
    /// </remarks>
    public DiarizationFrameBatch ReadNewFrames()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return ReadNewFramesCore(_handle, _speakerCount, _secondsPerFrame, _frameCursor);
        }
    }

    private DiarizationSnapshot GetSnapshotCore(DiarizationSegmentationOptions? segmentationOptions)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        NativeSegmentationConfig? nativeSegmentation = segmentationOptions?.ToNativeConfig();
        return ReadSnapshot(_handle, _speakerCount, _secondsPerFrame, _finished, nativeSegmentation);
    }

    private static DiarizationFrameBatch ReadNewFramesCore(StreamHandle handle, int speakerCount,
        double secondsPerFrame, IncrementalFrameCursor cursor)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, handle);
        long frameCount = NativeMethods.DiarFrameCount(handle);
        long firstFrame = NativeMethods.DiarFrameProbsStart(handle);
        if (frameCount < 0 || firstFrame < 0 || firstFrame > frameCount)
            throw new InvalidOperationException("The native library returned invalid frame bounds.");
        if (speakerCount <= 0 || !double.IsFinite(secondsPerFrame) || secondsPerFrame <= 0)
            throw new InvalidOperationException("The stream contains invalid native frame metadata.");

        cursor.ValidateWindow(firstFrame, frameCount);

        long retainedFrames = frameCount - firstFrame;
        if (retainedFrames > Array.MaxLength / speakerCount)
            throw new InvalidOperationException("The retained probability buffer exceeds managed array limits.");
        int floatCount = checked((int)(retainedFrames * speakerCount));
        float[] retainedProbabilities = new float[floatCount];
        if (retainedProbabilities.Length > 0)
        {
            NativeStatus status = NativeMethods.DiarFrameProbs(handle, retainedProbabilities,
                (nuint)retainedProbabilities.Length);
            if (status != NativeStatus.Ok)
                throw NativeCallException.Create(status);
        }

        return cursor.CreateBatch(firstFrame, frameCount, speakerCount, secondsPerFrame,
            retainedProbabilities);
    }

    internal static DiarizationSnapshot ReadSnapshot(StreamHandle handle, int speakerCount,
        double secondsPerFrame, bool isFinal, NativeSegmentationConfig? segmentationConfig = null)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, handle);
        long frameCount = NativeMethods.DiarFrameCount(handle);
        long firstFrame = NativeMethods.DiarFrameProbsStart(handle);
        if (frameCount < 0 || firstFrame < 0 || firstFrame > frameCount)
            throw new InvalidOperationException("The native library returned invalid frame bounds.");

        long retainedFrames = frameCount - firstFrame;
        if (retainedFrames > Array.MaxLength / speakerCount)
            throw new InvalidOperationException("The retained probability buffer exceeds managed array limits.");
        int floatCount = checked((int)(retainedFrames * speakerCount));

        float[] probabilities = new float[floatCount];
        if (probabilities.Length > 0)
        {
            NativeStatus probabilityStatus = NativeMethods.DiarFrameProbs(handle, probabilities, (nuint)probabilities.Length);
            if (probabilityStatus != NativeStatus.Ok)
                throw NativeCallException.Create(probabilityStatus);
        }

        NativeStatus countStatus = ReadSegments(handle, segmentationConfig, null, 0, out nuint segmentCount);
        if (countStatus != NativeStatus.Ok)
            throw NativeCallException.Create(countStatus);
        if (segmentCount > (nuint)Array.MaxLength)
            throw new InvalidOperationException("The native library returned too many segments.");

        NativeSegment[] nativeSegments = new NativeSegment[(int)segmentCount];
        if (nativeSegments.Length > 0)
        {
            NativeStatus segmentsStatus = ReadSegments(handle, segmentationConfig, nativeSegments,
                (nuint)nativeSegments.Length, out segmentCount);
            if (segmentsStatus != NativeStatus.Ok)
                throw NativeCallException.Create(segmentsStatus);
            if (segmentCount != (nuint)nativeSegments.Length)
                throw new InvalidOperationException("The native library returned an inconsistent segment count.");
        }

        var segments = new DiarizationSegment[nativeSegments.Length];
        double previousStart = -1;
        for (int index = 0; index < nativeSegments.Length; index++)
        {
            NativeSegment segment = nativeSegments[index];
            if (!double.IsFinite(segment.StartTime) || !double.IsFinite(segment.EndTime)
                || segment.StartTime < 0 || segment.EndTime < segment.StartTime
                || segment.StartTime < previousStart)
                throw new InvalidOperationException("The native library returned invalid or unordered segment timestamps.");
            if (segment.Speaker < 1 || segment.Speaker > speakerCount)
                throw new InvalidOperationException($"The native library returned an invalid 1-based speaker ID: {segment.Speaker}.");

            segments[index] = new DiarizationSegment(segment.StartTime, segment.EndTime, segment.Speaker);
            previousStart = segment.StartTime;
        }

        foreach (float probability in probabilities)
        {
            if (!float.IsFinite(probability) || probability is < 0 or > 1)
                throw new InvalidOperationException("The native library returned a speaker probability outside [0, 1].");
        }

        return new DiarizationSnapshot(firstFrame, frameCount, speakerCount, secondsPerFrame, probabilities, segments, isFinal);
    }

    private static NativeStatus ReadSegments(StreamHandle handle, NativeSegmentationConfig? config,
        NativeSegment[]? segments, nuint capacity, out nuint count)
    {
        if (!config.HasValue)
            return NativeMethods.DiarSegments(handle, 0, segments, capacity, out count);

        NativeSegmentationConfig nativeConfig = config.Value;
        return NativeMethods.DiarSegmentsWithConfig(handle, in nativeConfig, segments, capacity, out count);
    }

    public void Dispose()
    {
        lock (_sync)
            _handle.Dispose();
    }
}

public sealed record DiarizationSegment(double StartTime, double EndTime, int Speaker);

public sealed class DiarizationSnapshot
{
    private readonly float[] _probabilities;

    internal DiarizationSnapshot(long firstProbabilityFrame, long frameCount, int speakerCount,
        double secondsPerFrame, float[] probabilities, DiarizationSegment[] segments, bool isFinal)
    {
        if (firstProbabilityFrame < 0 || frameCount < firstProbabilityFrame)
            throw new InvalidOperationException("The snapshot contains invalid frame bounds.");
        if (speakerCount <= 0 || speakerCount > Array.MaxLength)
            throw new InvalidOperationException("The snapshot contains an invalid speaker count.");
        if (!double.IsFinite(secondsPerFrame) || secondsPerFrame <= 0)
            throw new InvalidOperationException("The snapshot contains an invalid frame duration.");
        ArgumentNullException.ThrowIfNull(probabilities);
        ArgumentNullException.ThrowIfNull(segments);

        long retainedFrames = frameCount - firstProbabilityFrame;
        if (retainedFrames > Array.MaxLength / speakerCount
            || probabilities.LongLength != retainedFrames * speakerCount)
            throw new InvalidOperationException("The snapshot probability buffer does not match its frame bounds.");

        FirstProbabilityFrame = firstProbabilityFrame;
        FrameCount = frameCount;
        IsFinal = isFinal;
        SpeakerCount = speakerCount;
        SecondsPerFrame = secondsPerFrame;
        _probabilities = probabilities;
        Segments = Array.AsReadOnly(segments);
    }

    public long FirstProbabilityFrame { get; }
    public long FrameCount { get; }
    public bool IsFinal { get; }
    public int SpeakerCount { get; }
    public double SecondsPerFrame { get; }
    public IReadOnlyList<DiarizationSegment> Segments { get; }

    public float GetProbability(long frame, int speakerIndex)
    {
        if (frame < FirstProbabilityFrame || frame >= FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frame), "Frame is outside the retained probability window.");
        if ((uint)speakerIndex >= (uint)SpeakerCount)
            throw new ArgumentOutOfRangeException(nameof(speakerIndex));

        long offset = checked((frame - FirstProbabilityFrame) * SpeakerCount + speakerIndex);
        return _probabilities[checked((int)offset)];
    }

    public double GetFrameTimestamp(long frame)
    {
        if (frame < 0 || frame >= FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frame));
        double timestamp = frame * SecondsPerFrame;
        if (!double.IsFinite(timestamp))
            throw new InvalidOperationException("The frame timestamp exceeds the supported numeric range.");
        return timestamp;
    }
}

internal sealed class NativeCallException : InvalidOperationException
{
    private NativeCallException(NativeStatus status, string message) : base($"NeMo-Speech.cpp call failed ({status}): {message}") { }

    internal static NativeCallException Create(NativeStatus status)
    {
        string error = NativeMethods.GetLastError();
        return new NativeCallException(status, error);
    }
}
