using NemotronDiarizationSharp.Native;
using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class PresetTests
{
    [Theory]
    [InlineData(DiarizationPresets.Streaming, "streaming", 268_435_215)]
    [InlineData(DiarizationPresets.Offline, "offline", 268_435_043)]
    [InlineData(DiarizationPresets.V3Streaming, "v3-streaming", 268_435_110)]
    [InlineData(DiarizationPresets.V3Offline, "v3-offline", 268_435_189)]
    public void ConstantsMatchPinnedNamesAndExistingValidator(string preset, string nativeName, int boundary)
    {
        Assert.Equal(nativeName, preset);
        var options = new DiarizationModelOptions { Preset = preset, ChunkFrames = boundary };
        Assert.Equal(nativeName, options.Preset);
        options.ValidateGeometryArithmetic();
        var config = new DiarModelConfig();
        options.ApplyNativeOverrides(ref config);
        Assert.Equal(boundary, config.ChunkFrames);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DiarizationModelOptions { Preset = preset, ChunkFrames = boundary + 1 }
                .ValidateGeometryArithmetic());
    }

    [Theory]
    [InlineData(DiarizationPresets.Streaming)]
    [InlineData(DiarizationPresets.Offline)]
    [InlineData(DiarizationPresets.V3Streaming)]
    [InlineData(DiarizationPresets.V3Offline)]
    public void ConstantsPreserveSentinelsAndExplicitLeftZero(string preset)
    {
        var options = new DiarizationModelOptions
        {
            Preset = preset, ChunkFrames = null, RightContextFrames = 0,
            LeftContextFrames = 0, FifoFrames = -1, SpeakerCacheFrames = int.MinValue,
            UpdatePeriodFrames = int.MaxValue
        };
        options.ValidateGeometryArithmetic();
        var config = new DiarModelConfig();
        options.ApplyNativeOverrides(ref config);
        Assert.Equal(0, config.ChunkFrames);
        Assert.Equal(0, config.RightContextFrames);
        Assert.Equal(0, config.LeftContextFrames);
        Assert.Equal(-1, config.FifoFrames);
        Assert.Equal(int.MinValue, config.SpeakerCacheFrames);
        Assert.Equal(int.MaxValue, config.UpdatePeriodFrames);
    }

    [Theory]
    [InlineData(DiarizationPresets.Streaming)]
    [InlineData(DiarizationPresets.Offline)]
    [InlineData(DiarizationPresets.V3Streaming)]
    [InlineData(DiarizationPresets.V3Offline)]
    public void ConstantsDoNotBypassOverflowValidation(string preset)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DiarizationModelOptions { Preset = preset, FifoFrames = int.MaxValue }
                .ValidateGeometryArithmetic());

    [Theory]
    [InlineData(DiarizationPresets.Nemotron3OfflineStyle, 264, 40, 340, 40, 300)]
    [InlineData(DiarizationPresets.Nemotron3LowLatency, 264, 264, 9, 4, 222)]
    [InlineData(DiarizationPresets.Nemotron3VeryLowLatency, 264, 264, 6, 2, 222)]
    [InlineData(DiarizationPresets.Nemotron3UltraLowLatency, 264, 264, 3, 1, 222)]
    public void Nemotron3ProfilesMapToV3StreamingAndApplyOfficialGeometry(
        string profile, int cache, int fifo, int chunk, int rightContext, int updatePeriod)
    {
        var options = new DiarizationModelOptions { Preset = profile };
        Assert.Equal(DiarizationPresets.V3Streaming, options.GetNativePreset());
        options.ValidateGeometryArithmetic();

        var config = new DiarModelConfig();
        options.ApplyNativeOverrides(ref config);
        Assert.Equal(chunk, config.ChunkFrames);
        Assert.Equal(rightContext, config.RightContextFrames);
        Assert.Equal(0, config.LeftContextFrames);
        Assert.Equal(fifo, config.FifoFrames);
        Assert.Equal(cache, config.SpeakerCacheFrames);
        Assert.Equal(updatePeriod, config.UpdatePeriodFrames);
    }

    [Fact]
    public void Nemotron3ProfileGeometryCanBeCustomizedWithoutChangingTheProfileName()
    {
        var options = new DiarizationModelOptions
        {
            Preset = DiarizationPresets.Nemotron3LowLatency,
            ChunkFrames = 12,
            RightContextFrames = 5,
            FifoFrames = 200,
            SpeakerCacheFrames = 300,
            UpdatePeriodFrames = 100
        };
        var config = new DiarModelConfig();

        options.ApplyNativeOverrides(ref config);

        Assert.Equal(DiarizationPresets.Nemotron3LowLatency, options.Preset);
        Assert.Equal(DiarizationPresets.V3Streaming, options.GetNativePreset());
        Assert.Equal(12, config.ChunkFrames);
        Assert.Equal(5, config.RightContextFrames);
        Assert.Equal(200, config.FifoFrames);
        Assert.Equal(300, config.SpeakerCacheFrames);
        Assert.Equal(100, config.UpdatePeriodFrames);
    }
}
