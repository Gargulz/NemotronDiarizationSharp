namespace NemotronDiarizationSharp.Evaluation;

/// <summary>A speaker interval from an RTTM file. Times are seconds on the caller's chosen timeline.</summary>
public sealed record RttmSegment
{
    public RttmSegment(string recordingId, string channelId, double startSeconds,
        double durationSeconds, string speakerLabel)
        : this(recordingId, channelId, startSeconds, durationSeconds, speakerLabel, allowNegativeStart: false)
    {
    }

    private RttmSegment(string recordingId, string channelId, double startSeconds,
        double durationSeconds, string speakerLabel, bool allowNegativeStart)
    {
        ValidateToken(recordingId, nameof(recordingId));
        ValidateToken(channelId, nameof(channelId));
        ValidateToken(speakerLabel, nameof(speakerLabel));
        if (!double.IsFinite(startSeconds) || (!allowNegativeStart && startSeconds < 0))
            throw new ArgumentOutOfRangeException(nameof(startSeconds), "Start time must be finite and nonnegative.");
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Duration must be finite and positive.");
        if (!double.IsFinite(startSeconds + durationSeconds))
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "The segment end time must be finite.");

        RecordingId = recordingId;
        ChannelId = channelId;
        StartSeconds = startSeconds;
        DurationSeconds = durationSeconds;
        SpeakerLabel = speakerLabel;
    }

    public string RecordingId { get; }
    public string ChannelId { get; }
    public double StartSeconds { get; }
    public double DurationSeconds { get; }
    public string SpeakerLabel { get; }
    public double EndSeconds => StartSeconds + DurationSeconds;

    /// <summary>Returns a copy whose start is offset from <paramref name="originSeconds"/>.</summary>
    public RttmSegment ToRelative(double originSeconds)
    {
        ValidateOrigin(originSeconds);
        double relativeStart = StartSeconds - originSeconds;
        if (!double.IsFinite(relativeStart))
            throw new ArgumentOutOfRangeException(nameof(originSeconds), "The relative start time must be finite.");
        return new RttmSegment(RecordingId, ChannelId, relativeStart, DurationSeconds, SpeakerLabel,
            allowNegativeStart: true);
    }

    /// <summary>Returns a copy whose start is offset onto the timeline beginning at <paramref name="originSeconds"/>.</summary>
    public RttmSegment ToAbsolute(double originSeconds)
    {
        ValidateOrigin(originSeconds);
        double absoluteStart = StartSeconds + originSeconds;
        if (!double.IsFinite(absoluteStart) || absoluteStart < 0)
            throw new ArgumentOutOfRangeException(nameof(originSeconds), "The absolute start time must be finite and nonnegative.");
        return new RttmSegment(RecordingId, ChannelId, absoluteStart, DurationSeconds, SpeakerLabel);
    }

    internal static string FormatTimestamp(double seconds) =>
        seconds.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

    internal static void ValidateOrigin(double originSeconds)
    {
        if (!double.IsFinite(originSeconds) || originSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(originSeconds), "The time origin must be finite and nonnegative.");
    }

    private static void ValidateToken(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Any(char.IsWhiteSpace))
            throw new ArgumentException("RTTM identifiers must not contain whitespace.", parameterName);
    }
}
