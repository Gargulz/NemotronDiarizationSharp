namespace NemotronDiarizationSharp;

/// <summary>Owned, optionally bounded history of diarization speaker probabilities.</summary>
public sealed class DiarizationProbabilityHistory
{
    private readonly int? _maxRetainedFrames;
    private float[] _values = [];
    private int _head;
    private int _count;
    private int? _speakerCount;
    private double _secondsPerFrame;
    private long _endFrame;
    private bool _hasPosition;
    private Guid? _sourceId;
    private long _lastSequence;

    /// <param name="maxRetainedFrames">Positive frame limit, or null for explicitly unbounded in-memory retention.</param>
    public DiarizationProbabilityHistory(int? maxRetainedFrames)
    {
        if (maxRetainedFrames is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetainedFrames));
        _maxRetainedFrames = maxRetainedFrames;
    }

    public long FirstRetainedFrame => _hasPosition ? _endFrame - _count : 0;
    public long EndFrameExclusive => _hasPosition ? _endFrame : 0;
    public bool IsCompleteFromFrameZero => _hasPosition && FirstRetainedFrame == 0;
    public int RetainedFrameCount => _count;
    public int? SpeakerCount => _speakerCount;
    public double? SecondsPerFrame => _speakerCount.HasValue ? _secondsPerFrame : null;
    public bool IsBoundToSource => _sourceId.HasValue;

    /// <summary>Appends externally restored, contiguous owned data before live update binding.</summary>
    public void Append(DiarizationFrameBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (_sourceId.HasValue)
            throw new InvalidOperationException("Restored batches can only be appended before binding to a live source.");
        ValidateBatch(batch);
        long batchEnd = checked(batch.FirstFrameIndex + batch.FrameCount);

        if (_speakerCount.HasValue)
        {
            ValidateMetadata(batch.SpeakerCount, batch.SecondsPerFrame);
            if (batch.FirstFrameIndex != _endFrame)
                throw new InvalidOperationException("Restored batches must be exactly contiguous.");
        }
        else if (batch.FrameCount == 0)
        {
            // An empty batch validates its metadata but does not establish frame coverage.
            return;
        }

        PrepareCapacity(checked(_count + batch.FrameCount), batch.SpeakerCount);
        if (!_speakerCount.HasValue)
        {
            _speakerCount = batch.SpeakerCount;
            _secondsPerFrame = batch.SecondsPerFrame;
            _endFrame = batch.FirstFrameIndex;
            _hasPosition = true;
        }
        AppendValues(batch, 0, batch.FrameCount);
        _endFrame = batchEnd;
        EvictExcess();
    }

    /// <summary>Applies one consecutive update atomically, including any reported revisions.</summary>
    public void Apply(DiarizationResultUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ValidateUpdate(update);
        DiarizationFrameBatch added = update.NewFrames;
        long newEnd = checked(added.FirstFrameIndex + added.FrameCount);

        if (_sourceId.HasValue)
        {
            if (update.SourceId != _sourceId.Value)
                throw new InvalidOperationException("The update belongs to another managed source.");
            if (update.Sequence != checked(_lastSequence + 1))
                throw new InvalidOperationException("Updates must be applied in consecutive sequence order.");
        }
        if (_speakerCount.HasValue)
        {
            ValidateMetadata(update.SpeakerCount, update.SecondsPerFrame);
            if (added.FirstFrameIndex != _endFrame)
                throw new InvalidOperationException("The new-frame range is discontinuous with retained history.");
        }

        int nextCount = checked(_count + added.FrameCount);
        PrepareCapacity(nextCount, update.SpeakerCount);

        // Everything that can be rejected is checked above. The mutations below only assign validated values.
        if (!_speakerCount.HasValue)
        {
            _speakerCount = update.SpeakerCount;
            _secondsPerFrame = update.SecondsPerFrame;
            _endFrame = added.FirstFrameIndex;
            _hasPosition = true;
        }

        long retainedStart = FirstRetainedFrame;
        foreach (DiarizationFrameBatch revision in update.RevisedFrames)
        {
            long revisionEnd = checked(revision.FirstFrameIndex + revision.FrameCount);
            long start = Math.Max(retainedStart, revision.FirstFrameIndex);
            long end = Math.Min(_endFrame, revisionEnd);
            for (long frame = start; frame < end; frame++)
                WriteFrame(frame, revision, frame - revision.FirstFrameIndex);
        }

        AppendValues(added, 0, added.FrameCount);
        _endFrame = newEnd;
        EvictExcess();
        _sourceId ??= update.SourceId;
        _lastSequence = update.Sequence;
    }

    /// <summary>Returns an owned copy of the currently retained window.</summary>
    public DiarizationFrameBatch GetFrames()
    {
        if (!_speakerCount.HasValue)
            throw new InvalidOperationException("No nonempty probability history has established metadata.");
        int floatCount = checked(_count * _speakerCount.Value);
        var copy = new float[floatCount];
        for (int frame = 0; frame < _count; frame++)
            CopyFrameTo(frame, copy, frame * _speakerCount.Value);
        return DiarizationFrameBatch.FromProbabilities(FirstRetainedFrame, _speakerCount.Value,
            _secondsPerFrame, copy);
    }

    private void ValidateUpdate(DiarizationResultUpdate update)
    {
        if (update.SourceId == Guid.Empty || update.Sequence <= 0)
            throw new InvalidOperationException("Update source and sequence metadata are invalid.");
        DiarizationFrameBatch.ValidateMetadata(update.FirstRetainedFrame, update.SpeakerCount, update.SecondsPerFrame);
        if (update.FrameCount < update.FirstRetainedFrame)
            throw new InvalidOperationException("Update frame bounds are invalid.");
        if (update.HistoryGap.HasValue)
            throw new InvalidOperationException("Updates with a reported history gap cannot be applied to contiguous history.");
        if (update.NewFrames is null || update.RevisedFrames is null)
            throw new InvalidOperationException("Update payloads are missing.");
        DiarizationFrameRange? compared = update.ComparedFrames;
        if (compared.HasValue && (compared.Value.StartFrame < update.FirstRetainedFrame
            || compared.Value.EndFrameExclusive <= compared.Value.StartFrame
            || compared.Value.EndFrameExclusive > update.FrameCount
            || compared.Value.EndFrameExclusive > update.NewFrames.FirstFrameIndex))
            throw new InvalidOperationException("The comparison range is outside the update's retained frame bounds.");
        ValidateUpdateBatch(update.NewFrames, update.SpeakerCount, update.SecondsPerFrame);
        long newEnd = checked(update.NewFrames.FirstFrameIndex + update.NewFrames.FrameCount);
        if (newEnd != update.FrameCount || update.NewFrames.FirstFrameIndex < update.FirstRetainedFrame)
            throw new InvalidOperationException("The new-frame range does not match update bounds.");

        long previousEnd = -1;
        foreach (DiarizationFrameBatch revision in update.RevisedFrames)
        {
            if (revision is null)
                throw new InvalidOperationException("A revision batch is null.");
            ValidateUpdateBatch(revision, update.SpeakerCount, update.SecondsPerFrame);
            long revisionEnd = checked(revision.FirstFrameIndex + revision.FrameCount);
            if (revision.FrameCount == 0 || revision.FirstFrameIndex < update.FirstRetainedFrame
                || revisionEnd > update.NewFrames.FirstFrameIndex || revision.FirstFrameIndex < previousEnd)
                throw new InvalidOperationException("Revision ranges must be ordered, disjoint, retained, and precede new frames.");
            if (!compared.HasValue || revision.FirstFrameIndex < compared.Value.StartFrame
                || revisionEnd > compared.Value.EndFrameExclusive)
                throw new InvalidOperationException("Every revision range must be inside reported comparison coverage.");
            previousEnd = revisionEnd;
        }
    }

    private static void ValidateUpdateBatch(DiarizationFrameBatch batch, int speakers, double cadence)
    {
        ValidateBatch(batch);
        if (batch.SpeakerCount != speakers || batch.SecondsPerFrame != cadence)
            throw new InvalidOperationException("Update batch metadata does not match its enclosing update.");
    }

    private static void ValidateBatch(DiarizationFrameBatch batch)
    {
        DiarizationFrameBatch.ValidateMetadata(batch.FirstFrameIndex, batch.SpeakerCount, batch.SecondsPerFrame);
        _ = checked(batch.FirstFrameIndex + batch.FrameCount);
        if (batch.FrameCount < 0 || batch.Probabilities.Count != checked(batch.FrameCount * batch.SpeakerCount))
            throw new InvalidOperationException("Batch values do not match its frame bounds.");
        foreach (float value in batch.Probabilities)
            if (!float.IsFinite(value) || value is < 0 or > 1)
                throw new InvalidOperationException("Batch probabilities must be finite values in [0, 1].");
    }

    private void ValidateMetadata(int speakers, double cadence)
    {
        if (_speakerCount != speakers || _secondsPerFrame != cadence)
            throw new InvalidOperationException("Speaker count and frame cadence must remain unchanged.");
    }

    private void PrepareCapacity(int requestedFrames, int speakers)
    {
        int maximum = _maxRetainedFrames ?? Array.MaxLength;
        int desired = Math.Min(requestedFrames, maximum);
        if (desired > Array.MaxLength / speakers)
            throw new InvalidOperationException("Probability history exceeds managed array limits.");
        int required = checked(desired * speakers);
        if (_values.Length >= required)
            return;
        int oldFrames = _speakerCount.HasValue ? _values.Length / speakers : 0;
        int capacityFrames = checked((int)Math.Min(maximum,
            Math.Max((long)desired, Math.Max(4L, (long)oldFrames * 2))));
        if (capacityFrames > Array.MaxLength / speakers)
            capacityFrames = Array.MaxLength / speakers;
        var grown = new float[checked(capacityFrames * speakers)];
        if (_speakerCount.HasValue)
            for (int frame = 0; frame < _count; frame++)
                CopyFrameTo(frame, grown, frame * speakers);
        _values = grown;
        _head = 0;
    }

    private void AppendValues(DiarizationFrameBatch batch, int firstBatchFrame, int frameCount)
    {
        int speakers = batch.SpeakerCount;
        for (int i = 0; i < frameCount; i++)
        {
            int slot = SlotFor(_count, speakers);
            int source = checked((firstBatchFrame + i) * speakers);
            for (int speaker = 0; speaker < speakers; speaker++)
                _values[slot * speakers + speaker] = batch.Probabilities[source + speaker];
            _count++;
            if (_maxRetainedFrames.HasValue && _count > _maxRetainedFrames.Value)
            {
                _head = (int)(((long)_head + 1) % (_values.Length / speakers));
                _count--;
            }
        }
    }

    private void WriteFrame(long absoluteFrame, DiarizationFrameBatch source, long sourceFrame)
    {
        int logical = checked((int)(absoluteFrame - FirstRetainedFrame));
        int slot = SlotFor(logical, _speakerCount!.Value);
        int sourceOffset = checked((int)sourceFrame * _speakerCount.Value);
        for (int speaker = 0; speaker < _speakerCount.Value; speaker++)
            _values[slot * _speakerCount.Value + speaker] = source.Probabilities[sourceOffset + speaker];
    }

    private void CopyFrameTo(int logicalFrame, float[] destination, int destinationOffset)
    {
        int slot = SlotFor(logicalFrame, _speakerCount!.Value);
        Array.Copy(_values, slot * _speakerCount.Value, destination, destinationOffset, _speakerCount.Value);
    }

    private void EvictExcess()
    {
        if (!_maxRetainedFrames.HasValue || _count <= _maxRetainedFrames.Value)
            return;
        int remove = _count - _maxRetainedFrames.Value;
        _head = (int)(((long)_head + remove) % (_values.Length / _speakerCount!.Value));
        _count -= remove;
    }

    private int SlotFor(int logicalFrame, int speakers) =>
        (int)(((long)_head + logicalFrame) % (_values.Length / speakers));
}
