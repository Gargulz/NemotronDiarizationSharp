using NemotronDiarizationSharp.Native;

namespace NemotronDiarizationSharp;

/// <summary>Optional device and streaming geometry passed to the native model constructor.</summary>
public sealed class DiarizationModelOptions
{
    /// <summary>
    /// CUDA device index to use. Null selects CPU. A nonnegative value explicitly selects that
    /// GPU and requires a CUDA-enabled native runtime; the SDK does not fall back to CPU.
    /// </summary>
    public int? GpuDeviceIndex { get; init; }

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

    internal int GetNativeGpuDeviceIndex()
    {
        if (GpuDeviceIndex is < 0)
            throw new ArgumentOutOfRangeException(nameof(GpuDeviceIndex), GpuDeviceIndex,
                "GPU device indexes must be nonnegative; use null to select CPU.");

        return GpuDeviceIndex ?? -1;
    }

    internal void ValidateGeometryArithmetic()
    {
        // The C ABI reads presets as NUL-terminated strings. Match its effective name
        // without changing the value sent to native code.
        string? nativePreset = Preset;
        if (nativePreset is not null)
        {
            int nulIndex = nativePreset.IndexOf('\0');
            if (nulIndex >= 0)
                nativePreset = nativePreset[..nulIndex];
        }

        switch (nativePreset)
        {
            case null:
            case "":
                // With no preset, the model chooses V2 or V3 streaming defaults.
                ValidateEffectiveGeometry(160, 80, 20, 0, 0);
                ValidateEffectiveGeometry(264, 80, 13, 0, 1);
                break;
            case "streaming":
                ValidateEffectiveGeometry(160, 80, 20, 0, 0);
                break;
            case "offline":
                ValidateEffectiveGeometry(312, 100, 100, 0, 0);
                break;
            case "v3-streaming":
                ValidateEffectiveGeometry(264, 80, 13, 0, 1);
                break;
            case "v3-offline":
                ValidateEffectiveGeometry(264, 0, 264, 1, 1);
                break;
            // Native code rejects unknown presets before creating the model.
        }
    }

    private void ValidateEffectiveGeometry(int cache, int fifo, int chunk, int left, int right)
    {
        long effectiveCache = SpeakerCacheFrames is > 0 ? SpeakerCacheFrames.Value : cache;
        long effectiveFifo = FifoFrames is > 0 ? FifoFrames.Value : fifo;
        long effectiveChunk = ChunkFrames is > 0 ? ChunkFrames.Value : chunk;
        long effectiveLeft = LeftContextFrames is >= 0 ? LeftContextFrames.Value : left;
        long effectiveRight = RightContextFrames is > 0 ? RightContextFrames.Value : right;
        long window = effectiveLeft + effectiveChunk + effectiveRight;
        long total = effectiveCache + effectiveFifo + window;

        // Pinned supported V3 checkpoint: encoder subsampling 8, output subsampling 1,
        // upsample factor 8, silence padding 3. V2 defaults: encoder/output subsampling 8,
        // upsample factor 1, silence padding 3.
        // total * 8 bounds state/window sums, output slices, and transient cache/FIFO
        // growth (including silence padding). window * 8 + 7 covers mel rounding.
        // The update period is clamped to the temporary FIFO before cache growth.
        const long factor = 8;
        if (total * factor > int.MaxValue || window * factor + factor - 1 > int.MaxValue)
            throw new ArgumentOutOfRangeException("options",
                "The effective streaming geometry exceeds signed native frame arithmetic for the supported factor-eight checkpoint.");
    }

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
