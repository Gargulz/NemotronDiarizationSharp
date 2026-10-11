namespace NemotronDiarizationSharp.Evaluation;

/// <summary>Configures a diarization score over one or more disjoint evaluation windows.</summary>
public sealed class DiarizationErrorRateOptions
{
    private static readonly IReadOnlyList<RttmTimeWindow> DefaultWindows =
        Array.AsReadOnly(new[] { new RttmTimeWindow(0, 180) });

    /// <summary>Uses the A2 default evaluation window, [0, 180] seconds.</summary>
    public DiarizationErrorRateOptions() : this(DefaultWindows)
    {
    }

    /// <summary>Uses the supplied absolute-time windows. Windows must be nonempty and nonoverlapping.</summary>
    public DiarizationErrorRateOptions(IEnumerable<RttmTimeWindow> evaluationWindows)
    {
        ArgumentNullException.ThrowIfNull(evaluationWindows);
        RttmTimeWindow[] windows = evaluationWindows.ToArray();
        if (windows.Length == 0)
            throw new ArgumentException("At least one evaluation window is required.", nameof(evaluationWindows));
        if (windows.Any(static window => window is null))
            throw new ArgumentException("Evaluation windows cannot contain null values.", nameof(evaluationWindows));

        Array.Sort(windows, static (left, right) => left.StartSeconds.CompareTo(right.StartSeconds));
        for (int index = 1; index < windows.Length; index++)
        {
            if (windows[index].StartSeconds < windows[index - 1].EndSeconds)
                throw new ArgumentException("Evaluation windows must not overlap.", nameof(evaluationWindows));
        }

        EvaluationWindows = Array.AsReadOnly(windows);
    }

    /// <summary>Gets the immutable, sorted set of evaluation windows.</summary>
    public IReadOnlyList<RttmTimeWindow> EvaluationWindows { get; }
}
