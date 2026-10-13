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

    /// <summary>NVIDIA Nemotron 3's 30.4-second streaming profile (officially labeled very high latency/offline).</summary>
    public const string Nemotron3OfflineStyle = "nemotron3-offline-style";

    /// <summary>NVIDIA Nemotron 3's 1.04-second streaming profile.</summary>
    public const string Nemotron3LowLatency = "nemotron3-low-latency";

    /// <summary>NVIDIA Nemotron 3's 0.64-second streaming profile.</summary>
    public const string Nemotron3VeryLowLatency = "nemotron3-very-low-latency";

    /// <summary>NVIDIA Nemotron 3's 0.32-second streaming profile.</summary>
    public const string Nemotron3UltraLowLatency = "nemotron3-ultra-low-latency";
}
