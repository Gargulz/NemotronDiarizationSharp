namespace NemotronDiarizationSharp;

/// <summary>Explicit managed thresholds and timing rules for custom segmentation.</summary>
public sealed class DiarizationPostprocessingOptions
{
    public float Onset { get; init; } = 0.5f;
    public float Offset { get; init; } = 0.5f;
    public double PadOnsetSeconds { get; init; }
    public double PadOffsetSeconds { get; init; }
    public double MinGapSeconds { get; init; }
    public double MinDurationSeconds { get; init; }

    internal void Validate()
    {
        if (!float.IsFinite(Onset) || !float.IsFinite(Offset) || Offset < 0 || Onset > 1 || Offset > Onset)
            throw new ArgumentOutOfRangeException(nameof(Onset), "Require finite thresholds with 0 <= Offset <= Onset <= 1.");
        ValidateDuration(PadOnsetSeconds, nameof(PadOnsetSeconds));
        ValidateDuration(PadOffsetSeconds, nameof(PadOffsetSeconds));
        ValidateDuration(MinGapSeconds, nameof(MinGapSeconds));
        ValidateDuration(MinDurationSeconds, nameof(MinDurationSeconds));
    }

    private static void ValidateDuration(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(name, "Durations must be finite and nonnegative.");
    }
}
