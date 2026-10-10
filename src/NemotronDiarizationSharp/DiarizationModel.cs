using System.Runtime.InteropServices;
using NemotronDiarizationSharp.Native;

namespace NemotronDiarizationSharp;

public sealed class DiarizationModel : IDisposable
{
    private readonly ModelHandle _handle;

    private DiarizationModel(ModelHandle handle)
    {
        _handle = handle;
        SpeakerCount = NativeMethods.DiarNumSpeakers(handle);
        SecondsPerFrame = NativeMethods.DiarSecondsPerFrame(handle);
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
                throw NativeCallException.Create(status);

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
            if (stream != 0)
                NativeMethods.DiarStreamClose(stream);
            if (modelReferenceAdded)
                _handle.DangerousRelease();
        }
    }

    public void Dispose() => _handle.Dispose();
}

public sealed class DiarizationStream : IDisposable
{
    private readonly StreamHandle _handle;
    private readonly object _sync = new();
    private readonly int _speakerCount;
    private readonly double _secondsPerFrame;
    private bool _finished;

    internal DiarizationStream(StreamHandle handle, int speakerCount, double secondsPerFrame)
    {
        _handle = handle;
        _speakerCount = speakerCount;
        _secondsPerFrame = secondsPerFrame;
    }

    public void Push(ReadOnlySpan<float> monoPcm, int sampleRate)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            if (_finished)
                throw new InvalidOperationException("The stream has already been finished.");
            if (sampleRate is < 8000 or > 96000)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be between 8 kHz and 96 kHz.");
            if (monoPcm.IsEmpty)
                return;

            float[] samples = monoPcm.ToArray();
            NativeStatus status = NativeMethods.DiarStreamPushF32(_handle, samples, (nuint)samples.Length, sampleRate);
            if (status != NativeStatus.Ok)
                throw NativeCallException.Create(status);
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
        long floatCount = checked(retainedFrames * _speakerCount);
        if (floatCount > int.MaxValue)
            throw new InvalidOperationException("The retained probability buffer exceeds managed array limits.");

        float[] probabilities = new float[(int)floatCount];
        if (probabilities.Length > 0)
        {
            NativeStatus probabilityStatus = NativeMethods.DiarFrameProbs(_handle, probabilities, (nuint)probabilities.Length);
            if (probabilityStatus != NativeStatus.Ok)
                throw NativeCallException.Create(probabilityStatus);
        }

        NativeStatus countStatus = NativeMethods.DiarSegments(_handle, 0, null, 0, out nuint segmentCount);
        if (countStatus != NativeStatus.Ok)
            throw NativeCallException.Create(countStatus);
        if (segmentCount > int.MaxValue)
            throw new InvalidOperationException("The native library returned too many segments.");

        NativeSegment[] nativeSegments = new NativeSegment[(int)segmentCount];
        if (nativeSegments.Length > 0)
        {
            NativeStatus segmentsStatus = NativeMethods.DiarSegments(_handle, 0, nativeSegments, (nuint)nativeSegments.Length, out segmentCount);
            if (segmentsStatus != NativeStatus.Ok)
                throw NativeCallException.Create(segmentsStatus);
            if (segmentCount != (nuint)nativeSegments.Length)
                Array.Resize(ref nativeSegments, checked((int)segmentCount));
        }

        DiarizationSegment[] segments = nativeSegments
            .Select(segment => new DiarizationSegment(segment.StartTime, segment.EndTime, segment.Speaker))
            .ToArray();
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
        return frame * SecondsPerFrame;
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
