namespace NemotronDiarizationSharp;

/// <summary>Implements the timestamp path used by NVIDIA NeMo Speech v3.0.0 Sortformer diarization.</summary>
public static class NvidiaNeMoReferencePostprocessor
{
    private const double TenMilliseconds = 0.01;

    /// <summary>Segments an owned probability batch using NeMo's v3.0.0 Sortformer defaults.</summary>
    public static IReadOnlyList<DiarizationSegment> Segment(DiarizationFrameBatch batch,
        DiarizationPostprocessingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var snapshot = new DiarizationSnapshot(batch.FirstFrameIndex,
            checked(batch.FirstFrameIndex + batch.FrameCount), batch.SpeakerCount, batch.SecondsPerFrame,
            batch.Probabilities.ToArray(), [], isFinal: true);
        return Segment(snapshot, options);
    }

    /// <summary>Segments a completed probability snapshot using NeMo's v3.0.0 Sortformer defaults.</summary>
    /// <remarks>
    /// The NeMo path repeats each model frame by the encoder subsampling factor, applies strict
    /// onset/offset comparisons, NeMo's final-frame end convention and default filtering, then
    /// rounds absolute timestamps to two decimal places. The factor is derived from the frame duration.
    /// </remarks>
    public static IReadOnlyList<DiarizationSegment> Segment(DiarizationSnapshot snapshot,
        DiarizationPostprocessingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new DiarizationPostprocessingOptions();
        options.Validate();

        double units = snapshot.SecondsPerFrame / TenMilliseconds;
        int subsamplingFactor = (int)Math.Round(units);
        if (subsamplingFactor <= 0 || Math.Abs(units - subsamplingFactor) > 1e-6)
            throw new InvalidOperationException("NeMo reference segmentation requires frame duration to be a whole number of 10 ms units.");

        long firstUnit = checked(snapshot.FirstProbabilityFrame * subsamplingFactor);
        long retainedFrames = snapshot.FrameCount - snapshot.FirstProbabilityFrame;
        long frameUnits = checked(retainedFrames * subsamplingFactor);
        double origin = firstUnit * TenMilliseconds;
        var result = new List<DiarizationSegment>();
        for (int speaker = 0; speaker < snapshot.SpeakerCount; speaker++)
        {
            var segments = new List<(double Start, double End)>();
            bool active = false;
            long activeStart = 0;
            for (long unit = 0; unit < frameUnits; unit++)
            {
                float probability = snapshot.GetProbability(
                    snapshot.FirstProbabilityFrame + unit / subsamplingFactor, speaker);
                if (active)
                {
                    if (probability < options.Offset)
                    {
                        AddSegment(segments, origin + activeStart * TenMilliseconds,
                            origin + unit * TenMilliseconds, options.PadOnsetSeconds, options.PadOffsetSeconds,
                            origin, includeZeroDuration: false);
                        active = false;
                    }
                }
                else if (probability > options.Onset)
                {
                    active = true;
                    activeStart = unit;
                }
            }
            if (active && frameUnits > 0)
                AddSegment(segments, origin + activeStart * TenMilliseconds,
                    origin + (frameUnits - 1) * TenMilliseconds, options.PadOnsetSeconds, options.PadOffsetSeconds,
                    origin, includeZeroDuration: true);

            segments = Merge(segments);
            if (options.MinDurationSeconds > 0)
                segments = segments.Where(s => s.End - s.Start >= options.MinDurationSeconds).ToList();
            if (options.MinGapSeconds > 0)
            {
                var filtered = new List<(double Start, double End)>();
                foreach (var segment in segments)
                {
                    if (filtered.Count > 0 && segment.Start - filtered[^1].End < options.MinGapSeconds)
                        filtered[^1] = (filtered[^1].Start, Math.Max(filtered[^1].End, segment.End));
                    else
                        filtered.Add(segment);
                }
                segments = filtered;
            }

            foreach (var segment in segments)
            {
                double start = Math.Round(segment.Start, 2);
                double end = Math.Round(segment.End, 2);
                if (end >= start)
                    result.Add(new DiarizationSegment(start, end, speaker + 1));
            }
        }
        result.Sort(static (a, b) =>
        {
            int order = a.StartTime.CompareTo(b.StartTime);
            if (order == 0) order = a.EndTime.CompareTo(b.EndTime);
            return order == 0 ? a.Speaker.CompareTo(b.Speaker) : order;
        });
        return Array.AsReadOnly(result.ToArray());
    }

    private static void AddSegment(List<(double Start, double End)> segments, double start, double end,
        double padOnset, double padOffset, double minimumStart, bool includeZeroDuration)
    {
        start = Math.Max(minimumStart, start - padOnset);
        end += padOffset;
        if (end > start || (includeZeroDuration && end == start))
            segments.Add((start, end));
    }

    private static List<(double Start, double End)> Merge(List<(double Start, double End)> segments)
    {
        if (segments.Count < 2)
            return segments;
        segments.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(double Start, double End)> { segments[0] };
        for (int i = 1; i < segments.Count; i++)
        {
            var previous = merged[^1];
            var current = segments[i];
            if (current.Start <= previous.End)
                merged[^1] = (previous.Start, Math.Max(previous.End, current.End));
            else
                merged.Add(current);
        }
        return merged;
    }
}
