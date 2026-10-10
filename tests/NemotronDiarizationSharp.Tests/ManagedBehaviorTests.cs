using System.Runtime.InteropServices;
using NemotronDiarizationSharp;
using NemotronDiarizationSharp.Native;
using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class ManagedBehaviorTests
{
    [Fact]
    public void SnapshotMapsRetainedProbabilityFramesAndTimestamps()
    {
        var snapshot = new DiarizationSnapshot(
            firstProbabilityFrame: 4,
            frameCount: 6,
            speakerCount: 2,
            secondsPerFrame: 0.01,
            probabilities: [0.1f, 0.2f, 0.3f, 0.4f],
            segments: [],
            isFinal: true);

        Assert.Equal(0.3f, snapshot.GetProbability(frame: 5, speakerIndex: 0));
        Assert.Equal(0.4f, snapshot.GetProbability(frame: 5, speakerIndex: 1));
        Assert.Equal(0.05, snapshot.GetFrameTimestamp(frame: 5), precision: 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetProbability(frame: 3, speakerIndex: 0));
    }

    [Fact]
    public void SnapshotRetainsOverlappingSpeakerSegments()
    {
        var first = new DiarizationSegment(1.0, 2.0, 1);
        var second = new DiarizationSegment(1.5, 2.5, 2);
        var snapshot = new DiarizationSnapshot(0, 10, 2, 0.1, new float[20], [first, second], isFinal: true);

        Assert.Equal(2, snapshot.Segments.Count);
        Assert.True(snapshot.Segments[0].StartTime < snapshot.Segments[1].EndTime
            && snapshot.Segments[1].StartTime < snapshot.Segments[0].EndTime);
        Assert.Equal(2, snapshot.Segments[1].Speaker);
    }

    [Fact]
    public void SnapshotRejectsProbabilityBufferThatDoesNotMatchRetainedFrames()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new DiarizationSnapshot(4, 6, 2, 0.01, [0.1f, 0.2f], [], isFinal: false));
    }

    [Fact]
    public void PushInputRequiresExplicitValidRateAndFiniteSamples()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationStream.ValidatePushInput([], 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationStream.ValidatePushInput([], 7999, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationStream.ValidatePushInput([], 96001, null));
        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidatePushInput([float.NaN], 16000, null));
        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidatePushInput([float.PositiveInfinity], 16000, null));
        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidatePushInput([float.NegativeInfinity], 16000, null));
    }

    [Fact]
    public void PushInputKeepsRateConstantAndAllowsEmptyNoOp()
    {
        DiarizationStream.ValidatePushInput([], 16000, null);
        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidatePushInput([], 48000, 16000));
        DiarizationStream.ValidatePushInput([-1f, 0f, 1f], 16000, 16000);

        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidatePushInput([0f], 48000, 16000));
    }

    [Fact]
    public void OfflineInputRequiresValidRateAndFiniteSamples()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationStream.ValidateOfflineInput([], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationStream.ValidateOfflineInput([], 7999));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationStream.ValidateOfflineInput([], 96001));
        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidateOfflineInput([float.NaN], 16000));
        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidateOfflineInput([float.PositiveInfinity], 16000));
        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidateOfflineInput([float.NegativeInfinity], 16000));

        DiarizationStream.ValidateOfflineInput([], 8000);
        DiarizationStream.ValidateOfflineInput([-1f, 0f, 1f], 96000);
    }

    [Fact]
    public void ModelOptionsPreserveNativeGeometrySentinels()
    {
        var defaults = new DiarModelConfig();
        new DiarizationModelOptions().ApplyNativeOverrides(ref defaults);

        Assert.Equal(0, defaults.ChunkFrames);
        Assert.Equal(0, defaults.RightContextFrames);
        Assert.Equal(-1, defaults.LeftContextFrames);
        Assert.Equal(0, defaults.FifoFrames);
        Assert.Equal(0, defaults.SpeakerCacheFrames);
        Assert.Equal(0, defaults.UpdatePeriodFrames);

        var overrides = new DiarModelConfig();
        new DiarizationModelOptions
        {
            ChunkFrames = 8,
            RightContextFrames = 2,
            LeftContextFrames = 0,
            FifoFrames = 32,
            SpeakerCacheFrames = 40,
            UpdatePeriodFrames = 4
        }.ApplyNativeOverrides(ref overrides);

        Assert.Equal(8, overrides.ChunkFrames);
        Assert.Equal(2, overrides.RightContextFrames);
        Assert.Equal(0, overrides.LeftContextFrames);
        Assert.Equal(32, overrides.FifoFrames);
        Assert.Equal(40, overrides.SpeakerCacheFrames);
        Assert.Equal(4, overrides.UpdatePeriodFrames);
    }

    [Fact]
    public void SegmentationOptionsPreserveNativeDefaultSentinelsAndRejectNonFiniteValues()
    {
        NativeSegmentationConfig defaults = new DiarizationSegmentationOptions().ToNativeConfig();
        Assert.Equal((nuint)Marshal.SizeOf<NativeSegmentationConfig>(), defaults.Size);
        Assert.Equal(0f, defaults.Onset);
        Assert.Equal(0f, defaults.Offset);
        Assert.Equal(0d, defaults.PadOnsetSec);
        Assert.Equal(0d, defaults.PadOffsetSec);
        Assert.Equal(0d, defaults.MinGapSec);
        Assert.Equal(0d, defaults.MinDurationSec);

        NativeSegmentationConfig mapped = new DiarizationSegmentationOptions
        {
            Onset = 0.7f,
            Offset = 0.4f,
            PadOnsetSeconds = 0.1,
            PadOffsetSeconds = 0.2,
            MinGapSeconds = 0.3,
            MinDurationSeconds = 0.5
        }.ToNativeConfig();

        Assert.Equal(0.7f, mapped.Onset);
        Assert.Equal(0.4f, mapped.Offset);
        Assert.Equal(0.1, mapped.PadOnsetSec);
        Assert.Equal(0.2, mapped.PadOffsetSec);
        Assert.Equal(0.3, mapped.MinGapSec);
        Assert.Equal(0.5, mapped.MinDurationSec);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiarizationSegmentationOptions { Onset = float.NaN }.ToNativeConfig());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiarizationSegmentationOptions { MinGapSeconds = double.PositiveInfinity }.ToNativeConfig());
    }

    [Fact]
    public void NativeConfigurationStructsMatchThePinnedX64AbiLayout()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(56, Marshal.SizeOf<DiarModelConfig>());
        Assert.Equal(48, Marshal.SizeOf<NativeSegmentationConfig>());

        Assert.Equal(0, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.Size)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.ModelPath)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.Gpu)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.Preset)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.ChunkFrames)).ToInt32());
        Assert.Equal(36, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.RightContextFrames)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.LeftContextFrames)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.FifoFrames)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.SpeakerCacheFrames)).ToInt32());
        Assert.Equal(52, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.UpdatePeriodFrames)).ToInt32());
        Assert.Equal(0, Marshal.OffsetOf<NativeSegmentationConfig>(nameof(NativeSegmentationConfig.Size)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<NativeSegmentationConfig>(nameof(NativeSegmentationConfig.Onset)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<NativeSegmentationConfig>(nameof(NativeSegmentationConfig.Offset)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<NativeSegmentationConfig>(nameof(NativeSegmentationConfig.PadOnsetSec)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<NativeSegmentationConfig>(nameof(NativeSegmentationConfig.PadOffsetSec)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<NativeSegmentationConfig>(nameof(NativeSegmentationConfig.MinGapSec)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<NativeSegmentationConfig>(nameof(NativeSegmentationConfig.MinDurationSec)).ToInt32());
    }

    [Fact]
    public void LoadRejectsMissingModelBeforeNativeLoading()
    {
        Assert.Throws<FileNotFoundException>(() => DiarizationModel.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".gguf")));
    }

    [Fact]
    public void NativeModelMetadataMustFitManagedResults()
    {
        DiarizationModel.ValidateNativeMetadata(4, 0.08);

        Assert.Throws<InvalidOperationException>(() => DiarizationModel.ValidateNativeMetadata(0, 0.08));
        Assert.Throws<InvalidOperationException>(() => DiarizationModel.ValidateNativeMetadata(-1, 0.08));
        Assert.Throws<InvalidOperationException>(() => DiarizationModel.ValidateNativeMetadata(int.MaxValue, 0.08));
        Assert.Throws<InvalidOperationException>(() => DiarizationModel.ValidateNativeMetadata(4, 0));
        Assert.Throws<InvalidOperationException>(() => DiarizationModel.ValidateNativeMetadata(4, double.NaN));
        Assert.Throws<InvalidOperationException>(() => DiarizationModel.ValidateNativeMetadata(4, double.PositiveInfinity));
    }
}
