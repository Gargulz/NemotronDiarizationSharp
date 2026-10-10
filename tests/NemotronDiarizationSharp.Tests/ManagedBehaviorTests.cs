using NemotronDiarizationSharp;
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
