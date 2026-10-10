using NemotronDiarizationSharp.Native;

namespace NemotronDiarizationSharp;

/// <summary>Optional CPU streaming geometry passed to the native model constructor.</summary>
public sealed class DiarizationModelOptions
{
    /// <summary>
    /// Streaming geometry preset: streaming, offline, v3-streaming, or v3-offline.
    /// Null leaves preset selection to the native model default. The offline names select
    /// streaming geometry and do not enable stateless full-attention inference.
    /// </summary>
    public string? Preset { get; init; }

    /// <summary>Chunk size in coarse 80 ms encoder frames; null or nonpositive keeps the preset value.</summary>
    public int? ChunkFrames { get; init; }

    /// <summary>Right context in coarse 80 ms encoder frames; null or nonpositive keeps the preset value.</summary>
    public int? RightContextFrames { get; init; }

    /// <summary>Left context in coarse 80 ms encoder frames; null keeps the preset value, while zero is an explicit override.</summary>
    public int? LeftContextFrames { get; init; }

    /// <summary>FIFO size in coarse 80 ms encoder frames; null or nonpositive keeps the preset value.</summary>
    public int? FifoFrames { get; init; }

    /// <summary>Speaker cache size in coarse 80 ms encoder frames; null or nonpositive keeps the preset value.</summary>
    public int? SpeakerCacheFrames { get; init; }

    /// <summary>Update period in coarse 80 ms encoder frames; null or nonpositive keeps the preset value.</summary>
    public int? UpdatePeriodFrames { get; init; }

    internal void ApplyNativeOverrides(ref DiarModelConfig config)
    {
        config.ChunkFrames = ChunkFrames ?? 0;
        config.RightContextFrames = RightContextFrames ?? 0;
        config.LeftContextFrames = LeftContextFrames ?? -1;
        config.FifoFrames = FifoFrames ?? 0;
        config.SpeakerCacheFrames = SpeakerCacheFrames ?? 0;
        config.UpdatePeriodFrames = UpdatePeriodFrames ?? 0;
    }
}
