using NemotronDiarizationSharp.Native;
using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class GeometryValidationTests
{
    [Theory]
    [InlineData("chunk")]
    [InlineData("right")]
    [InlineData("left")]
    [InlineData("fifo")]
    [InlineData("cache")]
    public void RejectsEachOverflowingExplicitGeometryField(string field)
    {
        var options = new DiarizationModelOptions
        {
            Preset = "v3-streaming",
            ChunkFrames = field == "chunk" ? int.MaxValue : null,
            RightContextFrames = field == "right" ? int.MaxValue : null,
            LeftContextFrames = field == "left" ? int.MaxValue : null,
            FifoFrames = field == "fifo" ? int.MaxValue : null,
            SpeakerCacheFrames = field == "cache" ? int.MaxValue : null
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.ValidateGeometryArithmetic());
    }

    [Fact]
    public void RejectsJointOverflowEvenWhenIndividualOverridesAreSafe()
    {
        const int frames = 150_000_000;
        new DiarizationModelOptions { ChunkFrames = frames }.ValidateGeometryArithmetic();
        new DiarizationModelOptions { SpeakerCacheFrames = frames }.ValidateGeometryArithmetic();

        var combined = new DiarizationModelOptions { ChunkFrames = frames, SpeakerCacheFrames = frames };
        Assert.Throws<ArgumentOutOfRangeException>(() => combined.ValidateGeometryArithmetic());
    }

    [Theory]
    [InlineData("streaming", 268_435_215)]
    [InlineData("offline", 268_435_043)]
    [InlineData("v3-streaming", 268_435_110)]
    [InlineData("v3-offline", 268_435_189)]
    public void AcceptsExactNativeProductBoundaryAndRejectsNextFrame(string preset, int chunk)
    {
        new DiarizationModelOptions { Preset = preset, ChunkFrames = chunk }.ValidateGeometryArithmetic();

        var overflow = new DiarizationModelOptions { Preset = preset, ChunkFrames = chunk + 1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => overflow.ValidateGeometryArithmetic());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\0offline")]
    public void UnspecifiedPresetChecksBothModelDefaultCandidates(string? preset)
    {
        const int v3Boundary = 268_435_110;
        new DiarizationModelOptions { Preset = preset, ChunkFrames = v3Boundary }.ValidateGeometryArithmetic();

        var overflow = new DiarizationModelOptions { Preset = preset, ChunkFrames = v3Boundary + 1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => overflow.ValidateGeometryArithmetic());
        // The same override is still arithmetic-safe with the smaller V2 default.
        new DiarizationModelOptions { Preset = "streaming", ChunkFrames = v3Boundary + 1 }
            .ValidateGeometryArithmetic();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void PreservesNativeSentinelsWithoutMutatingOptions(int? sentinel)
    {
        var options = new DiarizationModelOptions
        {
            Preset = "v3-offline",
            ChunkFrames = sentinel,
            RightContextFrames = sentinel,
            LeftContextFrames = sentinel,
            FifoFrames = sentinel,
            SpeakerCacheFrames = sentinel,
            UpdatePeriodFrames = sentinel
        };
        options.ValidateGeometryArithmetic();
        var config = new DiarModelConfig();
        options.ApplyNativeOverrides(ref config);

        Assert.Equal(sentinel ?? 0, config.ChunkFrames);
        Assert.Equal(sentinel ?? 0, config.RightContextFrames);
        Assert.Equal(sentinel ?? -1, config.LeftContextFrames);
        Assert.Equal(sentinel ?? 0, config.FifoFrames);
        Assert.Equal(sentinel ?? 0, config.SpeakerCacheFrames);
        Assert.Equal(sentinel ?? 0, config.UpdatePeriodFrames);
        Assert.Equal(sentinel, options.ChunkFrames);
        Assert.Equal(sentinel, options.RightContextFrames);
        Assert.Equal(sentinel, options.LeftContextFrames);
        Assert.Equal(sentinel, options.FifoFrames);
        Assert.Equal(sentinel, options.SpeakerCacheFrames);
        Assert.Equal(sentinel, options.UpdatePeriodFrames);
    }

    [Fact]
    public void ExplicitLeftZeroOverridesPresetLeftOneAtArithmeticBoundary()
    {
        const int cache = 268_435_453;
        var zeroLeft = new DiarizationModelOptions
        {
            Preset = "v3-offline", SpeakerCacheFrames = cache, ChunkFrames = 1, LeftContextFrames = 0
        };
        zeroLeft.ValidateGeometryArithmetic();
        var config = new DiarModelConfig();
        zeroLeft.ApplyNativeOverrides(ref config);
        Assert.Equal(0, config.LeftContextFrames);

        var presetLeft = new DiarizationModelOptions
        {
            Preset = "v3-offline", SpeakerCacheFrames = cache, ChunkFrames = 1
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => presetLeft.ValidateGeometryArithmetic());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("streaming")]
    [InlineData("offline")]
    [InlineData("v3-streaming")]
    [InlineData("v3-offline")]
    public void AcceptsKnownPresetsAndUnspecifiedDefaults(string? preset)
        => new DiarizationModelOptions { Preset = preset }.ValidateGeometryArithmetic();

    [Fact]
    public void MaximumUpdatePeriodIsSafeAndPreservedBecauseNativeClampsIt()
    {
        var options = new DiarizationModelOptions { UpdatePeriodFrames = int.MaxValue };
        options.ValidateGeometryArithmetic();
        var config = new DiarModelConfig();
        options.ApplyNativeOverrides(ref config);
        Assert.Equal(int.MaxValue, config.UpdatePeriodFrames);
    }

    [Theory]
    [InlineData("unrecognized")]
    [InlineData("unrecognized\0v3-streaming")]
    public void LeavesUnknownPresetRejectionToNative(string preset)
        => new DiarizationModelOptions { Preset = preset, ChunkFrames = int.MaxValue }
            .ValidateGeometryArithmetic();

    [Theory]
    [InlineData("streaming\0unrecognized")]
    [InlineData("v3-offline\0unrecognized")]
    public void ValidatesNativePresetPrefixWithoutChangingManagedString(string preset)
    {
        var options = new DiarizationModelOptions { Preset = preset, ChunkFrames = int.MaxValue };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.ValidateGeometryArithmetic());
        Assert.Equal(preset, options.Preset);
    }

    [Fact]
    public void LoadRejectsGeometryBeforeMissingFileOrNativeLoading()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".gguf");
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationModel.Load(missingPath,
            new DiarizationModelOptions { ChunkFrames = int.MaxValue }));
    }

    [Fact]
    public void LargeArithmeticSafeGeometryIsLeftForNativeModelSpecificValidation()
    {
        var options = new DiarizationModelOptions
        {
            ChunkFrames = 1_000_000, LeftContextFrames = 1_000_000,
            RightContextFrames = 1_000_000, FifoFrames = 1_000_000,
            SpeakerCacheFrames = 1_000_000
        };
        options.ValidateGeometryArithmetic();
    }
}
