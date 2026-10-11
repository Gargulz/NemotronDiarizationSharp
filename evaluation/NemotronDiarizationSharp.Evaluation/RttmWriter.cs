namespace NemotronDiarizationSharp.Evaluation;

/// <summary>Writes speaker intervals as canonical RTTM SPEAKER records.</summary>
public static class RttmWriter
{
    /// <summary>Writes deterministic output without taking ownership of <paramref name="writer"/>.</summary>
    public static void Write(TextWriter writer, IEnumerable<RttmSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(segments);
        RttmSegment[] ordered = segments
            .Select(segment => segment ?? throw new ArgumentException("Segments must not contain null values.", nameof(segments)))
            .OrderBy(segment => segment.StartSeconds)
            .ThenBy(segment => segment.RecordingId, StringComparer.Ordinal)
            .ThenBy(segment => segment.SpeakerLabel, StringComparer.Ordinal)
            .ThenBy(segment => segment.ChannelId, StringComparer.Ordinal)
            .ThenBy(segment => segment.DurationSeconds)
            .ToArray();

        foreach (RttmSegment segment in ordered)
        {
            if (segment.StartSeconds < 0)
                throw new ArgumentException("RTTM output cannot contain a negative start time.", nameof(segments));
            if (RttmSegment.FormatTimestamp(segment.DurationSeconds) == "0.000000")
                throw new ArgumentException("Segment duration rounds to zero at RTTM's six-decimal output precision.", nameof(segments));
        }

        foreach (RttmSegment segment in ordered)
        {
            writer.Write("SPEAKER ");
            writer.Write(segment.RecordingId);
            writer.Write(' ');
            writer.Write(segment.ChannelId);
            writer.Write(' ');
            writer.Write(RttmSegment.FormatTimestamp(segment.StartSeconds));
            writer.Write(' ');
            writer.Write(RttmSegment.FormatTimestamp(segment.DurationSeconds));
            writer.Write(" <NA> <NA> ");
            writer.Write(segment.SpeakerLabel);
            writer.WriteLine(" <NA> <NA>");
        }
    }
}
