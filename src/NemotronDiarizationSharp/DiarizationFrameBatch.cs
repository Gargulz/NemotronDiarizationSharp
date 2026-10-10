namespace NemotronDiarizationSharp;

/// <summary>An owned batch of newly read, frame-major speaker probabilities.</summary>
public sealed class DiarizationFrameBatch
{
    private readonly float[] _probabilities;

    internal DiarizationFrameBatch(long firstFrameIndex, int frameCount, int speakerCount,
        double secondsPerFrame, float[] probabilities)
    {
        FirstFrameIndex = firstFrameIndex;
        FrameCount = frameCount;
        SpeakerCount = speakerCount;
        SecondsPerFrame = secondsPerFrame;
        _probabilities = probabilities;
        Probabilities = Array.AsReadOnly(probabilities);
    }

    /// <summary>Absolute index of the first frame in this batch.</summary>
    public long FirstFrameIndex { get; }

    /// <summary>Number of frames in this batch.</summary>
    public int FrameCount { get; }

    public int SpeakerCount { get; }
    public double SecondsPerFrame { get; }

    /// <summary>Read-only frame-major values: frame 0 speakers, then frame 1 speakers, and so on.</summary>
    public IReadOnlyList<float> Probabilities { get; }

    /// <summary>Gets a probability by absolute frame index and zero-based speaker index.</summary>
    public float GetProbability(long frameIndex, int speakerIndex)
    {
        if (frameIndex < FirstFrameIndex || frameIndex >= checked(FirstFrameIndex + FrameCount))
            throw new ArgumentOutOfRangeException(nameof(frameIndex), "Frame is outside this batch.");
        if ((uint)speakerIndex >= (uint)SpeakerCount)
            throw new ArgumentOutOfRangeException(nameof(speakerIndex));

        long offset = checked((frameIndex - FirstFrameIndex) * SpeakerCount + speakerIndex);
        return _probabilities[checked((int)offset)];
    }
}

/// <summary>Raised when native compaction removed frames that had not yet been read.</summary>
public sealed class DiarizationFrameHistoryGapException : InvalidOperationException
{
    internal DiarizationFrameHistoryGapException(long missingStartFrame, long missingEndFrameExclusive,
        long earliestAvailableFrame)
        : base($"Native diarization history no longer contains unread frames " +
               $"[{missingStartFrame}, {missingEndFrameExclusive}); earliest available frame is " +
               $"{earliestAvailableFrame}.")
    {
        MissingStartFrame = missingStartFrame;
        MissingEndFrameExclusive = missingEndFrameExclusive;
        EarliestAvailableFrame = earliestAvailableFrame;
    }

    public long MissingStartFrame { get; }
    public long MissingEndFrameExclusive { get; }
    public long EarliestAvailableFrame { get; }
}

/// <summary>Per-stream cursor logic, isolated so compaction behavior can be tested deterministically.</summary>
internal sealed class IncrementalFrameCursor
{
    internal long NextUnreadFrame { get; private set; }

    internal void ValidateWindow(long firstRetainedFrame, long frameCount)
    {
        if (firstRetainedFrame < 0 || frameCount < firstRetainedFrame)
            throw new InvalidOperationException("The native library returned invalid frame bounds.");
        if (NextUnreadFrame > frameCount)
            throw new InvalidOperationException("The native frame count moved behind the incremental cursor.");
        if (NextUnreadFrame < firstRetainedFrame)
            throw new DiarizationFrameHistoryGapException(NextUnreadFrame, firstRetainedFrame,
                firstRetainedFrame);
    }

    internal DiarizationFrameBatch CreateBatch(long firstRetainedFrame, long frameCount, int speakerCount,
        double secondsPerFrame, float[] retainedProbabilities)
    {
        ArgumentNullException.ThrowIfNull(retainedProbabilities);
        if (firstRetainedFrame < 0 || frameCount < firstRetainedFrame)
            throw new InvalidOperationException("The native library returned invalid frame bounds.");
        if (speakerCount <= 0 || speakerCount > Array.MaxLength)
            throw new InvalidOperationException("The native library returned an invalid speaker count.");
        if (!double.IsFinite(secondsPerFrame) || secondsPerFrame <= 0)
            throw new InvalidOperationException("The native library returned an invalid frame duration.");

        long retainedFrames = frameCount - firstRetainedFrame;
        if (retainedFrames > Array.MaxLength / speakerCount)
            throw new InvalidOperationException("The retained probability buffer exceeds managed array limits.");
        int expectedFloatCount = checked((int)(retainedFrames * speakerCount));
        if (retainedProbabilities.Length != expectedFloatCount)
            throw new InvalidOperationException("The retained probability buffer does not match its frame bounds.");

        ValidateWindow(firstRetainedFrame, frameCount);

        long newFrames = frameCount - NextUnreadFrame;
        if (newFrames > Array.MaxLength / speakerCount)
            throw new InvalidOperationException("The unread probability batch exceeds managed array limits.");
        int batchFloatCount = checked((int)(newFrames * speakerCount));
        int sourceOffset = checked((int)((NextUnreadFrame - firstRetainedFrame) * speakerCount));
        var probabilities = new float[batchFloatCount];
        if (batchFloatCount > 0)
            Array.Copy(retainedProbabilities, sourceOffset, probabilities, 0, batchFloatCount);

        foreach (float probability in probabilities)
        {
            if (!float.IsFinite(probability) || probability is < 0 or > 1)
                throw new InvalidOperationException("The native library returned a speaker probability outside [0, 1].");
        }

        int batchFrameCount = checked((int)newFrames);
        var batch = new DiarizationFrameBatch(NextUnreadFrame, batchFrameCount, speakerCount,
            secondsPerFrame, probabilities);
        NextUnreadFrame = frameCount;
        return batch;
    }
}
