namespace NemotronDiarizationSharp.Evaluation;

/// <summary>
/// Computes boundary-based diarization error rate using the pyannote.metrics 4.1 protocol
/// (release commit 5b197b1): zero collar, overlap retained, and global Hungarian assignment.
/// </summary>
public static class DiarizationErrorRateScorer
{
    /// <summary>Scores reference and hypothesis segments over the configured windows.</summary>
    public static DiarizationErrorRateScore Score(
        IEnumerable<RttmSegment> reference,
        IEnumerable<RttmSegment> hypothesis,
        DiarizationErrorRateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(hypothesis);
        options ??= new DiarizationErrorRateOptions();

        RttmSegment[] referenceSegments = Materialize(reference, nameof(reference));
        RttmSegment[] hypothesisSegments = Materialize(hypothesis, nameof(hypothesis));
        ValidateRecordingIds(referenceSegments, hypothesisSegments);

        List<Interval> referenceIntervals = ClipSegments(referenceSegments, options.EvaluationWindows);
        List<Interval> hypothesisIntervals = ClipSegments(hypothesisSegments, options.EvaluationWindows);
        string[] referenceLabels = referenceIntervals.Select(static interval => interval.Speaker)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] hypothesisLabels = hypothesisIntervals.Select(static interval => interval.Speaker)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        List<Boundary> boundaries = CreateBoundaries(referenceIntervals, hypothesisIntervals);
        double[,] cooccurrence = BuildCooccurrence(boundaries, referenceLabels, hypothesisLabels);
        Dictionary<string, string> mapping = BuildMapping(referenceLabels, hypothesisLabels, cooccurrence);

        double falseAlarm = 0;
        double missed = 0;
        double confusion = 0;
        double total = 0;
        double correct = 0;
        double referenceOverlap = 0;
        double hypothesisOverlap = 0;
        var activeReference = new Dictionary<string, int>(StringComparer.Ordinal);
        var activeHypothesis = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Boundary boundary in boundaries)
        {
            ApplyCounts(boundary, activeReference, add: false, referenceSide: true);
            ApplyCounts(boundary, activeHypothesis, add: false, referenceSide: false);
            ApplyCounts(boundary, activeReference, add: true, referenceSide: true);
            ApplyCounts(boundary, activeHypothesis, add: true, referenceSide: false);

            double duration = boundary.NextTime - boundary.Time;
            if (duration <= 0)
                continue;

            int referenceCount = activeReference.Values.Sum();
            int hypothesisCount = activeHypothesis.Values.Sum();
            int matched = 0;
            foreach ((string hypothesisLabel, int hypothesisTrackCount) in activeHypothesis)
            {
                if (mapping.TryGetValue(hypothesisLabel, out string? referenceLabel)
                    && activeReference.TryGetValue(referenceLabel, out int referenceTrackCount))
                {
                    matched += Math.Min(hypothesisTrackCount, referenceTrackCount);
                }
            }

            int pairedCount = Math.Min(referenceCount, hypothesisCount);
            correct += duration * matched;
            confusion += duration * (pairedCount - matched);
            missed += duration * Math.Max(0, referenceCount - hypothesisCount);
            falseAlarm += duration * Math.Max(0, hypothesisCount - referenceCount);
            total += duration * referenceCount;
            if (activeReference.Count > 1)
                referenceOverlap += duration;
            if (activeHypothesis.Count > 1)
                hypothesisOverlap += duration;
        }

        return new DiarizationErrorRateScore(falseAlarm, missed, confusion, total, correct,
            referenceLabels.Length, hypothesisLabels.Length, referenceOverlap, hypothesisOverlap, mapping);
    }

    private static RttmSegment[] Materialize(IEnumerable<RttmSegment> segments, string parameterName)
    {
        RttmSegment[] result = segments.ToArray();
        if (result.Any(static segment => segment is null))
            throw new ArgumentException("Annotations cannot contain null segments.", parameterName);
        return result;
    }

    private static void ValidateRecordingIds(RttmSegment[] reference, RttmSegment[] hypothesis)
    {
        string[] referenceIds = reference.Select(static segment => segment.RecordingId)
            .Distinct(StringComparer.Ordinal).ToArray();
        string[] hypothesisIds = hypothesis.Select(static segment => segment.RecordingId)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (referenceIds.Length > 1 || hypothesisIds.Length > 1)
            throw new ArgumentException("Each annotation must contain segments from only one recording.");
        if (referenceIds.Length == 1 && hypothesisIds.Length == 1
            && !string.Equals(referenceIds[0], hypothesisIds[0], StringComparison.Ordinal))
        {
            throw new ArgumentException("Reference and hypothesis recording identifiers must match.");
        }
    }

    private static List<Interval> ClipSegments(
        IEnumerable<RttmSegment> segments,
        IReadOnlyList<RttmTimeWindow> windows)
    {
        // Keep each RTTM record as a track instance for IdentificationErrorRate's
        // non-unique label matching; only the mapping matrix below unions by label.
        var clippedSegments = new List<Interval>();
        foreach (RttmSegment segment in segments)
        {
            foreach (RttmTimeWindow window in windows)
            {
                RttmSegment? clipped = window.Clip(segment);
                if (clipped is null)
                    continue;
                // Use Clip for the shared intersection/empty semantics, but keep the
                // absolute endpoints to avoid a relative/absolute round-trip changing
                // the original RTTM double precision.
                double start = Math.Max(segment.StartSeconds, window.StartSeconds);
                double end = Math.Min(segment.EndSeconds, window.EndSeconds);
                clippedSegments.Add(new Interval(start, end, segment.SpeakerLabel));
            }
        }
        return clippedSegments;
    }

    private static List<Boundary> CreateBoundaries(
        IEnumerable<Interval> reference,
        IEnumerable<Interval> hypothesis)
    {
        var events = new SortedDictionary<double, Boundary>();
        foreach ((IEnumerable<Interval> intervals, bool referenceSide) in new[]
                 { (reference, true), (hypothesis, false) })
        {
            foreach (Interval interval in intervals)
            {
                Boundary start = GetBoundary(events, interval.Start);
                Boundary end = GetBoundary(events, interval.End);
                (referenceSide ? start.ReferenceStarts : start.HypothesisStarts).Add(interval.Speaker);
                (referenceSide ? end.ReferenceEnds : end.HypothesisEnds).Add(interval.Speaker);
            }
        }

        Boundary[] ordered = events.Values.ToArray();
        for (int index = 0; index + 1 < ordered.Length; index++)
            ordered[index].NextTime = ordered[index + 1].Time;
        return ordered.ToList();
    }

    private static Boundary GetBoundary(SortedDictionary<double, Boundary> events, double time)
    {
        if (!events.TryGetValue(time, out Boundary? boundary))
        {
            boundary = new Boundary(time);
            events.Add(time, boundary);
        }
        return boundary;
    }

    private static void ApplyCounts(
        Boundary boundary,
        Dictionary<string, int> active,
        bool add,
        bool referenceSide)
    {
        IEnumerable<string> speakers = add
            ? referenceSide ? boundary.ReferenceStarts : boundary.HypothesisStarts
            : referenceSide ? boundary.ReferenceEnds : boundary.HypothesisEnds;
        foreach (string speaker in speakers)
        {
            if (add)
                active[speaker] = active.TryGetValue(speaker, out int current) ? current + 1 : 1;
            else
            {
                int remaining = active[speaker] - 1;
                if (remaining == 0)
                    active.Remove(speaker);
                else
                    active[speaker] = remaining;
            }
        }
    }

    private static double[,] BuildCooccurrence(
        IReadOnlyList<Boundary> boundaries,
        IReadOnlyList<string> referenceLabels,
        IReadOnlyList<string> hypothesisLabels)
    {
        var refIndexes = referenceLabels.Select((label, index) => (label, index))
            .ToDictionary(static item => item.label, static item => item.index, StringComparer.Ordinal);
        var hypIndexes = hypothesisLabels.Select((label, index) => (label, index))
            .ToDictionary(static item => item.label, static item => item.index, StringComparer.Ordinal);
        var cooccurrence = new double[referenceLabels.Count, hypothesisLabels.Count];
        var activeReference = new Dictionary<string, int>(StringComparer.Ordinal);
        var activeHypothesis = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Boundary boundary in boundaries)
        {
            ApplyCounts(boundary, activeReference, add: false, referenceSide: true);
            ApplyCounts(boundary, activeHypothesis, add: false, referenceSide: false);
            ApplyCounts(boundary, activeReference, add: true, referenceSide: true);
            ApplyCounts(boundary, activeHypothesis, add: true, referenceSide: false);
            double duration = boundary.NextTime - boundary.Time;
            if (duration <= 0)
                continue;
            foreach (string referenceLabel in activeReference.Keys)
            foreach (string hypothesisLabel in activeHypothesis.Keys)
                cooccurrence[refIndexes[referenceLabel], hypIndexes[hypothesisLabel]] += duration;
        }
        return cooccurrence;
    }

    private static Dictionary<string, string> BuildMapping(
        IReadOnlyList<string> referenceLabels,
        IReadOnlyList<string> hypothesisLabels,
        double[,] cooccurrence)
    {
        int size = Math.Max(referenceLabels.Count, hypothesisLabels.Count);
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        if (size == 0)
            return mapping;

        var weights = new double[size, size];
        double maxWeight = 0;
        for (int reference = 0; reference < referenceLabels.Count; reference++)
        for (int hypothesis = 0; hypothesis < hypothesisLabels.Count; hypothesis++)
        {
            weights[reference, hypothesis] = cooccurrence[reference, hypothesis];
            maxWeight = Math.Max(maxWeight, weights[reference, hypothesis]);
        }

        int[] assignment = HungarianMaximize(weights, maxWeight);
        for (int reference = 0; reference < referenceLabels.Count; reference++)
        {
            int hypothesis = assignment[reference];
            if (hypothesis < hypothesisLabels.Count && weights[reference, hypothesis] > 0)
                mapping.Add(hypothesisLabels[hypothesis], referenceLabels[reference]);
        }
        return mapping;
    }

    // Square, deterministic O(n^3) Hungarian assignment. Rows are references,
    // columns are hypotheses; zero-overlap pairs are omitted from the public map.
    private static int[] HungarianMaximize(double[,] weights, double maximum)
    {
        int size = weights.GetLength(0);
        var rowPotential = new double[size + 1];
        var columnPotential = new double[size + 1];
        var columnRow = new int[size + 1];
        var previousColumn = new int[size + 1];

        for (int row = 1; row <= size; row++)
        {
            columnRow[0] = row;
            int currentColumn = 0;
            var minimum = Enumerable.Repeat(double.PositiveInfinity, size + 1).ToArray();
            var used = new bool[size + 1];
            do
            {
                used[currentColumn] = true;
                int currentRow = columnRow[currentColumn];
                double delta = double.PositiveInfinity;
                int nextColumn = 0;
                for (int column = 1; column <= size; column++)
                {
                    if (used[column])
                        continue;
                    double cost = maximum - weights[currentRow - 1, column - 1]
                        - rowPotential[currentRow] - columnPotential[column];
                    if (cost < minimum[column])
                    {
                        minimum[column] = cost;
                        previousColumn[column] = currentColumn;
                    }
                    if (minimum[column] < delta)
                    {
                        delta = minimum[column];
                        nextColumn = column;
                    }
                }
                for (int column = 0; column <= size; column++)
                {
                    if (used[column])
                    {
                        rowPotential[columnRow[column]] += delta;
                        columnPotential[column] -= delta;
                    }
                    else
                    {
                        minimum[column] -= delta;
                    }
                }
                currentColumn = nextColumn;
            }
            while (columnRow[currentColumn] != 0);

            do
            {
                int prior = previousColumn[currentColumn];
                columnRow[currentColumn] = columnRow[prior];
                currentColumn = prior;
            }
            while (currentColumn != 0);
        }

        var assignment = new int[size];
        for (int column = 1; column <= size; column++)
            assignment[columnRow[column] - 1] = column - 1;
        return assignment;
    }

    private sealed record Interval(double Start, double End, string Speaker);

    private sealed class Boundary(double time)
    {
        public double Time { get; } = time;
        public double NextTime { get; set; }
        public List<string> ReferenceStarts { get; } = [];
        public List<string> ReferenceEnds { get; } = [];
        public List<string> HypothesisStarts { get; } = [];
        public List<string> HypothesisEnds { get; } = [];
    }
}
