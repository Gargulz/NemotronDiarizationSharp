using NemotronSharp;
using Xunit;

namespace NemotronSharp.Tests;

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
        var snapshot = new DiarizationSnapshot(0, 10, 2, 0.1, [], [first, second], isFinal: true);

        Assert.Equal(2, snapshot.Segments.Count);
        Assert.True(snapshot.Segments[0].StartTime < snapshot.Segments[1].EndTime
            && snapshot.Segments[1].StartTime < snapshot.Segments[0].EndTime);
        Assert.Equal(2, snapshot.Segments[1].Speaker);
    }

    [Fact]
    public void LoadRejectsMissingModelBeforeNativeLoading()
    {
        Assert.Throws<FileNotFoundException>(() => DiarizationModel.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".gguf")));
    }
}
