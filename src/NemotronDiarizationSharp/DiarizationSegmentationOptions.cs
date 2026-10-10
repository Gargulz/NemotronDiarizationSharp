using System.Runtime.InteropServices;
using NemotronDiarizationSharp.Native;

namespace NemotronDiarizationSharp;

/// <summary>Optional threshold and postprocessing settings for segment extraction.</summary>
public sealed class DiarizationSegmentationOptions
{
    /// <summary>Speech-entry probability threshold; null or nonpositive selects the native default.</summary>
    public float? Onset { get; init; }

    /// <summary>Speech-exit probability threshold; null or nonpositive selects the native default.</summary>
    public float? Offset { get; init; }

    /// <summary>Seconds added before each segment; null or nonpositive selects the native default.</summary>
    public double? PadOnsetSeconds { get; init; }

    /// <summary>Seconds added after each segment; null or nonpositive selects the native default.</summary>
    public double? PadOffsetSeconds { get; init; }

    /// <summary>Silence gaps shorter than this many seconds are filled; null or nonpositive selects the native default.</summary>
    public double? MinGapSeconds { get; init; }

    /// <summary>Segments shorter than this many seconds are removed; null or nonpositive selects the native default.</summary>
    public double? MinDurationSeconds { get; init; }

    internal NativeSegmentationConfig ToNativeConfig()
    {
        ValidateFinite(Onset, nameof(Onset));
        ValidateFinite(Offset, nameof(Offset));
        ValidateFinite(PadOnsetSeconds, nameof(PadOnsetSeconds));
        ValidateFinite(PadOffsetSeconds, nameof(PadOffsetSeconds));
        ValidateFinite(MinGapSeconds, nameof(MinGapSeconds));
        ValidateFinite(MinDurationSeconds, nameof(MinDurationSeconds));

        return new NativeSegmentationConfig
        {
            Size = (nuint)Marshal.SizeOf<NativeSegmentationConfig>(),
            Onset = Onset ?? 0,
            Offset = Offset ?? 0,
            PadOnsetSec = PadOnsetSeconds ?? 0,
            PadOffsetSec = PadOffsetSeconds ?? 0,
            MinGapSec = MinGapSeconds ?? 0,
            MinDurationSec = MinDurationSeconds ?? 0
        };
    }

    private static void ValidateFinite(float? value, string propertyName)
    {
        if (value.HasValue && !float.IsFinite(value.Value))
            throw new ArgumentOutOfRangeException(propertyName, "Segmentation values must be finite.");
    }

    private static void ValidateFinite(double? value, string propertyName)
    {
        if (value.HasValue && !double.IsFinite(value.Value))
            throw new ArgumentOutOfRangeException(propertyName, "Segmentation values must be finite.");
    }
}
