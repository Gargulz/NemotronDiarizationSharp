using System.Globalization;

namespace NemotronDiarizationSharp;

/// <summary>Managed segmentation and RTTM output for owned probability batches.</summary>
public static class DiarizationPostprocessor
{
    public static IReadOnlyList<DiarizationSegment> Segment(DiarizationFrameBatch batch,
        DiarizationPostprocessingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        options ??= new DiarizationPostprocessingOptions();
        options.Validate();
        long endFrame = checked(batch.FirstFrameIndex + batch.FrameCount);
        double windowStart = batch.FirstFrameIndex * batch.SecondsPerFrame;
        double windowEnd = endFrame * batch.SecondsPerFrame;
        if (!double.IsFinite(windowStart) || !double.IsFinite(windowEnd))
            throw new ArgumentOutOfRangeException(nameof(batch), "Absolute frame timing exceeds finite seconds.");

        var result = new List<DiarizationSegment>();
        for (int speaker = 0; speaker < batch.SpeakerCount; speaker++)
        {
            var runs = new List<(long Start, long End)>();
            bool active = false;
            long activeStart = 0;
            for (int i = 0; i < batch.FrameCount; i++)
            {
                long frame = checked(batch.FirstFrameIndex + i);
                float probability = batch.GetProbability(frame, speaker);
                if (!active && probability > options.Onset)
                {
                    active = true;
                    activeStart = frame;
                }
                else if (active && probability < options.Offset)
                {
                    runs.Add((activeStart, frame));
                    active = false;
                }
            }
            if (active)
                runs.Add((activeStart, endFrame));

            if (runs.Count == 0)
                continue;

            var padded = new List<(double Start, double End)>(runs.Count);
            foreach (var run in runs)
            {
                double paddedStart = run.Start * batch.SecondsPerFrame - options.PadOnsetSeconds;
                double paddedEnd = run.End * batch.SecondsPerFrame + options.PadOffsetSeconds;
                if (!double.IsFinite(paddedStart) || !double.IsFinite(paddedEnd))
                    throw new ArgumentOutOfRangeException(nameof(options), "Padded absolute timing exceeds finite seconds.");
                double start = Math.Max(windowStart, paddedStart);
                double end = Math.Min(windowEnd, paddedEnd);
                padded.Add((start, end));
            }

            var merged = new List<(double Start, double End)> { padded[0] };
            for (int i = 1; i < padded.Count; i++)
            {
                var previous = merged[^1];
                var current = padded[i];
                if (current.Start - previous.End <= options.MinGapSeconds)
                    merged[^1] = (previous.Start, Math.Max(previous.End, current.End));
                else
                    merged.Add(current);
            }

            foreach (var segment in merged)
            {
                if (segment.End - segment.Start >= options.MinDurationSeconds)
                    result.Add(new DiarizationSegment(segment.Start, segment.End, speaker + 1));
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

    public static void WriteRttm(TextWriter writer, IEnumerable<DiarizationSegment> segments,
        string recordingId, int channel = 1)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);
        if (recordingId.Any(char.IsWhiteSpace))
            throw new ArgumentException("Recording IDs cannot contain whitespace.", nameof(recordingId));
        if (channel <= 0)
            throw new ArgumentOutOfRangeException(nameof(channel));

        foreach (DiarizationSegment segment in segments)
        {
            if (segment is null || !double.IsFinite(segment.StartTime) || !double.IsFinite(segment.EndTime)
                || segment.StartTime < 0 || segment.EndTime <= segment.StartTime || segment.Speaker <= 0)
                throw new ArgumentException("RTTM segments require finite nonnegative starts, positive durations, and one-based speakers.", nameof(segments));
            double duration = segment.EndTime - segment.StartTime;
            if (!double.IsFinite(duration))
                throw new ArgumentException("RTTM duration must be finite.", nameof(segments));
            writer.Write("SPEAKER ");
            writer.Write(recordingId);
            writer.Write(' ');
            writer.Write(channel.ToString(CultureInfo.InvariantCulture));
            writer.Write(' ');
            writer.Write(segment.StartTime.ToString("R", CultureInfo.InvariantCulture));
            writer.Write(' ');
            writer.Write(duration.ToString("R", CultureInfo.InvariantCulture));
            writer.Write(" <NA> <NA> speaker_");
            writer.Write(segment.Speaker.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(" <NA> <NA>");
        }
    }
}
