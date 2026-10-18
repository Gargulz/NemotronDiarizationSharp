using NemotronDiarizationSharp.Native;

namespace NemotronDiarizationSharp;

/// <summary>Backend, optional CUDA device, and streaming geometry passed to model initialization.</summary>
public sealed class DiarizationModelOptions
{
    /// <summary>Backend selection. Auto is GPU preferred and is the default.</summary>
    public DiarizationBackend Backend { get; init; } = DiarizationBackend.Auto;

    /// <summary>
    /// CUDA logical device ordinal to use. Null selects ordinal 0 when CUDA is selected. This
    /// setting does not select a backend; an explicit index is invalid if Auto selects CPU or
    /// Backend is Cpu. CUDA validates the ordinal during model initialization.
    /// </summary>
    public int? GpuDeviceIndex { get; init; }

    /// <summary>
    /// Streaming geometry preset: streaming, offline, v3-streaming, v3-offline, or one of the
    /// NVIDIA Nemotron 3 profiles on <see cref="DiarizationPresets"/>.
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

    /// <summary>Creates the published Nemotron 3 offline-style streaming geometry defaults.</summary>
    public static DiarizationModelOptions ForCompletedRecording() => new()
    {
        Preset = DiarizationPresets.Nemotron3OfflineStyle
    };

    /// <summary>Creates the published Nemotron 3 low-latency live-streaming geometry defaults.</summary>
    public static DiarizationModelOptions ForLiveStreaming() => new()
    {
        Preset = DiarizationPresets.Nemotron3LowLatency
    };

    internal DiarizationModelOptions WithDefaultPreset(string preset) => new()
    {
        Backend = Backend,
        GpuDeviceIndex = GpuDeviceIndex,
        Preset = Preset ?? preset,
        ChunkFrames = ChunkFrames,
        RightContextFrames = RightContextFrames,
        LeftContextFrames = LeftContextFrames,
        FifoFrames = FifoFrames,
        SpeakerCacheFrames = SpeakerCacheFrames,
        UpdatePeriodFrames = UpdatePeriodFrames
    };

    internal int GetNativeGpuDeviceIndex(DiarizationBackend selectedBackend)
    {
        if (GpuDeviceIndex is < 0)
            throw new ArgumentOutOfRangeException(nameof(GpuDeviceIndex), GpuDeviceIndex,
                "GPU device indexes must be nonnegative.");

        if (selectedBackend == DiarizationBackend.Cpu)
        {
            if (GpuDeviceIndex.HasValue)
                throw new InvalidOperationException("GpuDeviceIndex requires the CUDA backend; Backend=Cpu cannot use a GPU device index.");
            return -1;
        }

        return GpuDeviceIndex ?? 0;
    }

    internal void ValidateBackendOptions()
    {
        if (!Enum.IsDefined(Backend))
            throw new ArgumentOutOfRangeException(nameof(Backend), Backend, "Unknown diarization backend.");
        if (GpuDeviceIndex is < 0)
            throw new ArgumentOutOfRangeException(nameof(GpuDeviceIndex), GpuDeviceIndex,
                "GPU device indexes must be nonnegative.");
        if (Backend == DiarizationBackend.Cpu && GpuDeviceIndex.HasValue)
            throw new InvalidOperationException("GpuDeviceIndex requires the CUDA backend; Backend=Cpu cannot use a GPU device index.");
    }

    internal string? GetNativePreset()
        => TryGetNemotron3Geometry(out _) ? DiarizationPresets.V3Streaming : Preset;

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

        if (TryGetNemotron3Geometry(out var profile))
        {
            ValidateEffectiveGeometry(profile.Cache, profile.Fifo, profile.Chunk, profile.Left, profile.Right);
            return;
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
        if (TryGetNemotron3Geometry(out var profile))
        {
            config.ChunkFrames = ChunkFrames is > 0 ? ChunkFrames.Value : profile.Chunk;
            config.RightContextFrames = RightContextFrames is > 0 ? RightContextFrames.Value : profile.Right;
            config.LeftContextFrames = LeftContextFrames is >= 0 ? LeftContextFrames.Value : profile.Left;
            config.FifoFrames = FifoFrames is > 0 ? FifoFrames.Value : profile.Fifo;
            config.SpeakerCacheFrames = SpeakerCacheFrames is > 0 ? SpeakerCacheFrames.Value : profile.Cache;
            config.UpdatePeriodFrames = UpdatePeriodFrames is > 0 ? UpdatePeriodFrames.Value : profile.UpdatePeriod;
            return;
        }

        config.ChunkFrames = ChunkFrames ?? 0;
        config.RightContextFrames = RightContextFrames ?? 0;
        config.LeftContextFrames = LeftContextFrames ?? -1;
        config.FifoFrames = FifoFrames ?? 0;
        config.SpeakerCacheFrames = SpeakerCacheFrames ?? 0;
        config.UpdatePeriodFrames = UpdatePeriodFrames ?? 0;
    }

    private bool TryGetNemotron3Geometry(out (int Cache, int Fifo, int Chunk, int Left, int Right, int UpdatePeriod) geometry)
    {
        // NVIDIA's published values are streaming geometries. The pinned v0.2.0 runtime
        // accepts v3-streaming; the remaining published values are sent as existing overrides.
        geometry = Preset switch
        {
            DiarizationPresets.Nemotron3OfflineStyle => (264, 40, 340, 0, 40, 300),
            DiarizationPresets.Nemotron3LowLatency => (264, 264, 9, 0, 4, 222),
            DiarizationPresets.Nemotron3VeryLowLatency => (264, 264, 6, 0, 2, 222),
            DiarizationPresets.Nemotron3UltraLowLatency => (264, 264, 3, 0, 1, 222),
            _ => default
        };
        return geometry.Cache != 0;
    }
}
