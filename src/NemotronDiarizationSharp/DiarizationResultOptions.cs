namespace NemotronDiarizationSharp;

/// <summary>Chooses how a snapshot's segment list is produced.</summary>
public enum DiarizationSegmentationMode
{
    Native,
    Managed,
    NvidiaNeMoReference
}

/// <summary>Segmentation selection for high-level snapshot reads.</summary>
public sealed class DiarizationResultOptions
{
    public DiarizationSegmentationMode SegmentationMode { get; init; } = DiarizationSegmentationMode.Native;
    public DiarizationSegmentationOptions? NativeOptions { get; init; }
    public DiarizationPostprocessingOptions? ManagedOptions { get; init; }
    public DiarizationPostprocessingOptions? NeMoReferenceOptions { get; init; }

    internal void Validate()
    {
        if (!Enum.IsDefined(SegmentationMode))
            throw new ArgumentOutOfRangeException(nameof(SegmentationMode));
        ManagedOptions?.Validate();
        NeMoReferenceOptions?.Validate();
        if (SegmentationMode != DiarizationSegmentationMode.Native && NativeOptions is not null)
            throw new ArgumentException("Native segmentation options are only valid in Native mode.", nameof(NativeOptions));
        if (SegmentationMode != DiarizationSegmentationMode.Managed && ManagedOptions is not null)
            throw new ArgumentException("Managed options are only valid in Managed mode.", nameof(ManagedOptions));
        if (SegmentationMode != DiarizationSegmentationMode.NvidiaNeMoReference && NeMoReferenceOptions is not null)
            throw new ArgumentException("NeMo reference options are only valid in NVIDIA NeMo Reference mode.", nameof(NeMoReferenceOptions));
    }
}
