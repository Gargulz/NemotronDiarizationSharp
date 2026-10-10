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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        string fullPath = Path.GetFullPath(modelPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The diarization model file was not found.", fullPath);

        nint utf8Path = Marshal.StringToCoTaskMemUTF8(fullPath);
        try
        {
            var config = new DiarModelConfig
            {
                Size = (nuint)Marshal.SizeOf<DiarModelConfig>(),
                ModelPath = utf8Path,
                Gpu = -1,
                LeftContextFrames = -1
            };

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
    {
        lock (_sync)
        {
            return GetSnapshotCore();
        }
    }

    private DiarizationSnapshot GetSnapshotCore()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        long frameCount = NativeMethods.DiarFrameCount(_handle);
        long firstFrame = NativeMethods.DiarFrameProbsStart(_handle);
        if (frameCount < 0 || firstFrame < 0 || firstFrame > frameCount)
            throw new InvalidOperationException("The native library returned invalid frame bounds.");

        long retainedFrames = frameCount - firstFrame;
        if (retainedFrames > Array.MaxLength / _speakerCount)
            throw new InvalidOperationException("The retained probability buffer exceeds managed array limits.");
        int floatCount = checked((int)(retainedFrames * _speakerCount));

        float[] probabilities = new float[floatCount];
        if (probabilities.Length > 0)
        {
            NativeStatus probabilityStatus = NativeMethods.DiarFrameProbs(_handle, probabilities, (nuint)probabilities.Length);
            if (probabilityStatus != NativeStatus.Ok)
                throw NativeCallException.Create(probabilityStatus);
        }

        NativeStatus countStatus = NativeMethods.DiarSegments(_handle, 0, null, 0, out nuint segmentCount);
        if (countStatus != NativeStatus.Ok)
            throw NativeCallException.Create(countStatus);
        if (segmentCount > (nuint)Array.MaxLength)
            throw new InvalidOperationException("The native library returned too many segments.");

        NativeSegment[] nativeSegments = new NativeSegment[(int)segmentCount];
        if (nativeSegments.Length > 0)
        {
            NativeStatus segmentsStatus = NativeMethods.DiarSegments(_handle, 0, nativeSegments, (nuint)nativeSegments.Length, out segmentCount);
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
            if (segment.Speaker < 1 || segment.Speaker > _speakerCount)
                throw new InvalidOperationException($"The native library returned an invalid 1-based speaker ID: {segment.Speaker}.");

            segments[index] = new DiarizationSegment(segment.StartTime, segment.EndTime, segment.Speaker);
            previousStart = segment.StartTime;
        }

        foreach (float probability in probabilities)
        {
            if (!float.IsFinite(probability) || probability is < 0 or > 1)
                throw new InvalidOperationException("The native library returned a speaker probability outside [0, 1].");
        }

        return new DiarizationSnapshot(firstFrame, frameCount, _speakerCount, _secondsPerFrame, probabilities, segments, _finished);
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
