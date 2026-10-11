namespace NemotronDiarizationSharp.Evaluation;

/// <summary>An absolute-time interval used to clip RTTM segments. Clipped results use window-relative times.</summary>
public sealed record RttmTimeWindow
{
    public RttmTimeWindow(double startSeconds, double durationSeconds)
    {
        RttmSegment.ValidateOrigin(startSeconds);
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Window duration must be finite and positive.");
        if (!double.IsFinite(startSeconds + durationSeconds))
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Window end time must be finite.");
        StartSeconds = startSeconds;
        DurationSeconds = durationSeconds;
    }

    public double StartSeconds { get; }
    public double DurationSeconds { get; }
    public double EndSeconds => StartSeconds + DurationSeconds;

    /// <summary>Clips an absolute-time segment and returns its intersection relative to this window, or null if disjoint.</summary>
    public RttmSegment? Clip(RttmSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        double clippedStart = Math.Max(segment.StartSeconds, StartSeconds);
        double clippedEnd = Math.Min(segment.EndSeconds, EndSeconds);
        if (clippedEnd <= clippedStart)
            return null;
        return new RttmSegment(segment.RecordingId, segment.ChannelId,
            clippedStart - StartSeconds, clippedEnd - clippedStart, segment.SpeakerLabel);
    }
}
