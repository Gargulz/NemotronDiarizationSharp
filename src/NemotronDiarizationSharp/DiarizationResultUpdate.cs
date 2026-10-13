using NemotronDiarizationSharp.Native;

namespace NemotronDiarizationSharp;

/// <summary>An absolute frame interval with an exclusive end.</summary>
public readonly record struct DiarizationFrameRange(long StartFrame, long EndFrameExclusive)
{
    public long FrameCount => checked(EndFrameExclusive - StartFrame);
}

/// <summary>An owned incremental view of newly available and revised diarization results.</summary>
public sealed class DiarizationResultUpdate
{
    internal DiarizationResultUpdate(Guid sourceId, long sequence, long firstRetainedFrame, long frameCount,
        int speakerCount, double secondsPerFrame, bool isFinal, DiarizationFrameBatch newFrames,
        IReadOnlyList<DiarizationFrameBatch> revisedFrames, DiarizationSegment[] segments,
        DiarizationFrameRange? historyGap, DiarizationFrameRange? comparedFrames)
    {
        SourceId = sourceId;
        Sequence = sequence;
        FirstRetainedFrame = firstRetainedFrame;
        FrameCount = frameCount;
        SpeakerCount = speakerCount;
        SecondsPerFrame = secondsPerFrame;
        IsFinal = isFinal;
        NewFrames = newFrames;
        RevisedFrames = Array.AsReadOnly(revisedFrames.ToArray());
        Segments = Array.AsReadOnly(segments.ToArray());
        HistoryGap = historyGap;
        ComparedFrames = comparedFrames;
    }

    /// <summary>Identifies this managed update cursor; it is not a native stream identity.</summary>
    public Guid SourceId { get; }
    public long Sequence { get; }
    public long FirstRetainedFrame { get; }
    public long FrameCount { get; }
    public int SpeakerCount { get; }
    public double SecondsPerFrame { get; }
    public bool IsFinal { get; }
    public DiarizationFrameBatch NewFrames { get; }
    public IReadOnlyList<DiarizationFrameBatch> RevisedFrames { get; }
    /// <summary>The complete latest segment list, replacing the list from the prior update.</summary>
    public IReadOnlyList<DiarizationSegment> Segments { get; }
    public DiarizationFrameRange? HistoryGap { get; }
    public DiarizationFrameRange? ComparedFrames { get; }
}

/// <summary>Transactional comparison cursor for repeated retained probability windows.</summary>
internal sealed class ResultUpdateCursor
{
    private readonly Guid _sourceId = Guid.NewGuid();
    private float[] _cache = [];
    private float[] _scratch = [];
    private long _cacheFirstFrame;
    private long _cacheFrameCount;
    private long _lastFrameCount;
    private int _speakerCount;
    private double _secondsPerFrame;
    private bool _hasBaseline;

    internal long Sequence { get; private set; }
    internal long NextUnreadFrame { get; private set; }
    internal int CacheCapacity => _cache.Length;
    internal int ScratchCapacity => _scratch.Length;
    internal (object Cache, object Scratch) BufferIdentityForTesting => (_cache, _scratch);

    internal DiarizationResultUpdate Read(long firstRetainedFrame, long frameCount, int speakerCount,
        double secondsPerFrame, bool isFinal, bool allowHistoryGap,
        Func<float[], int, NativeStatus> readProbabilities, Func<DiarizationSegment[]> readSegments,
        Func<NativeStatus, Exception> nativeErrorFactory)
    {
        ArgumentNullException.ThrowIfNull(readProbabilities);
        ArgumentNullException.ThrowIfNull(readSegments);
        ArgumentNullException.ThrowIfNull(nativeErrorFactory);
        ValidateBounds(firstRetainedFrame, frameCount, speakerCount, secondsPerFrame);
        if (_hasBaseline)
        {
            if (frameCount < _lastFrameCount)
                throw new InvalidOperationException("The native frame count moved backwards.");
            if (speakerCount != _speakerCount || secondsPerFrame != _secondsPerFrame)
                throw new InvalidOperationException("The stream frame metadata changed between result reads.");
        }

        DiarizationFrameRange? gap = null;
        long newStart = NextUnreadFrame;
        if (newStart > frameCount)
            throw new InvalidOperationException("The native frame count moved behind the result cursor.");
        if (newStart < firstRetainedFrame)
        {
            if (!allowHistoryGap)
                throw new DiarizationFrameHistoryGapException(newStart, firstRetainedFrame, firstRetainedFrame);
            gap = new DiarizationFrameRange(newStart, firstRetainedFrame);
            newStart = firstRetainedFrame;
        }

        long retainedFrames = frameCount - firstRetainedFrame;
        if (retainedFrames > Array.MaxLength / speakerCount)
            throw new InvalidOperationException("The retained probability buffer exceeds managed array limits.");
        int floatCount = checked((int)(retainedFrames * speakerCount));
        EnsureCapacity(ref _scratch, floatCount);
        if (floatCount > 0)
        {
            NativeStatus status = readProbabilities(_scratch, floatCount);
            if (status != NativeStatus.Ok)
                throw nativeErrorFactory(status);
            foreach (float probability in _scratch.AsSpan(0, floatCount))
                if (!float.IsFinite(probability) || probability is < 0 or > 1)
                    throw new InvalidOperationException("The native library returned a speaker probability outside [0, 1].");
        }

        DiarizationSegment[] segments = readSegments() ??
            throw new InvalidOperationException("The result reader returned no segment collection.");
        ValidateSegments(segments, speakerCount);

        DiarizationFrameRange? compared = null;
        var revised = new List<DiarizationFrameBatch>();
        long overlapStart = Math.Max(firstRetainedFrame, _cacheFirstFrame);
        long overlapEnd = Math.Min(frameCount, checked(_cacheFirstFrame + _cacheFrameCount));
        if (_hasBaseline && overlapStart < overlapEnd)
        {
            compared = new DiarizationFrameRange(overlapStart, overlapEnd);
            BuildRevised(overlapStart, overlapEnd, firstRetainedFrame, speakerCount, floatCount, revised);
        }

        long newFrameCount = frameCount - newStart;
        if (newFrameCount > Array.MaxLength / speakerCount || newFrameCount > int.MaxValue)
            throw new InvalidOperationException("The new probability batch exceeds managed array limits.");
        int newFloatCount = checked((int)(newFrameCount * speakerCount));
        int newOffset = checked((int)((newStart - firstRetainedFrame) * speakerCount));
        var newValues = new float[newFloatCount];
        if (newFloatCount > 0)
            Array.Copy(_scratch, newOffset, newValues, 0, newFloatCount);
        var newBatch = new DiarizationFrameBatch(newStart, checked((int)newFrameCount), speakerCount,
            secondsPerFrame, newValues);

        long nextSequence = checked(Sequence + 1);
        var update = new DiarizationResultUpdate(_sourceId, nextSequence, firstRetainedFrame, frameCount,
            speakerCount, secondsPerFrame, isFinal, newBatch, revised, segments, gap, compared);

        (_cache, _scratch) = (_scratch, _cache);
        _cacheFirstFrame = firstRetainedFrame;
        _cacheFrameCount = retainedFrames;
        _lastFrameCount = frameCount;
        _speakerCount = speakerCount;
        _secondsPerFrame = secondsPerFrame;
        _hasBaseline = true;
        NextUnreadFrame = frameCount;
        Sequence = nextSequence;

        return update;

        void BuildRevised(long start, long end, long currentFirst, int speakers, int currentLength,
            List<DiarizationFrameBatch> batches)
        {
            long runStart = -1;
            var run = new List<float>();
            for (long frame = start; frame < end; frame++)
            {
                int currentOffset = checked((int)((frame - currentFirst) * speakers));
                int priorOffset = checked((int)((frame - _cacheFirstFrame) * speakers));
                bool changed = false;
                for (int speaker = 0; speaker < speakers; speaker++)
                    changed |= _scratch[currentOffset + speaker] != _cache[priorOffset + speaker];
                if (changed)
                {
                    if (runStart < 0)
                        runStart = frame;
                    for (int speaker = 0; speaker < speakers; speaker++)
                        run.Add(_scratch[currentOffset + speaker]);
                }
                else if (runStart >= 0)
                {
                    batches.Add(MakeBatch(runStart, run));
                    runStart = -1;
                    run = [];
                }
            }
            if (runStart >= 0)
                batches.Add(MakeBatch(runStart, run));
        }

        DiarizationFrameBatch MakeBatch(long start, List<float> values)
        {
            float[] owned = values.ToArray();
            return new DiarizationFrameBatch(start, owned.Length / speakerCount, speakerCount,
                secondsPerFrame, owned);
        }
    }

    private static void EnsureCapacity(ref float[] buffer, int length)
    {
        if (buffer.Length >= length)
            return;
        int capacity = checked((int)Math.Max(length,
            Math.Min((long)Array.MaxLength, Math.Max(16L, (long)buffer.Length * 2))));
        buffer = new float[capacity];
    }

    private static void ValidateBounds(long firstRetainedFrame, long frameCount, int speakerCount,
        double secondsPerFrame)
    {
        if (firstRetainedFrame < 0 || frameCount < firstRetainedFrame)
            throw new InvalidOperationException("The native library returned invalid frame bounds.");
        if (speakerCount <= 0 || speakerCount > Array.MaxLength || !double.IsFinite(secondsPerFrame)
            || secondsPerFrame <= 0)
            throw new InvalidOperationException("The stream contains invalid native frame metadata.");
    }

    private static void ValidateSegments(DiarizationSegment[] segments, int speakerCount)
    {
        double previousStart = -1;
        foreach (DiarizationSegment segment in segments)
        {
            if (!double.IsFinite(segment.StartTime) || !double.IsFinite(segment.EndTime)
                || segment.StartTime < 0 || segment.EndTime < segment.StartTime
                || segment.StartTime < previousStart || segment.Speaker < 1 || segment.Speaker > speakerCount)
                throw new InvalidOperationException("The native library returned invalid or unordered segments.");
            previousStart = segment.StartTime;
        }
    }
}
