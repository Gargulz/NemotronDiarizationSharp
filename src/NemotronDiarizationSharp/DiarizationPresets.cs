namespace NemotronDiarizationSharp;

/// <summary>Names for stateful streaming geometry presets accepted by the pinned native runtime.</summary>
/// <remarks>Stateless full-attention inference uses <see cref="DiarizationModel.ProcessOffline(ReadOnlySpan{float}, int)"/>.</remarks>
public static class DiarizationPresets
{
    /// <summary>V2 streaming geometry.</summary>
    public const string Streaming = "streaming";

    /// <summary>Larger V2 streaming geometry; does not select stateless offline inference.</summary>
    public const string Offline = "offline";

    /// <summary>V3 streaming geometry.</summary>
    public const string V3Streaming = "v3-streaming";

    /// <summary>Larger V3 streaming geometry; does not select stateless offline inference.</summary>
    public const string V3Offline = "v3-offline";
}
