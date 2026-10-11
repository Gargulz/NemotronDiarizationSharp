namespace NemotronDiarizationSharp;

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
