using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class NvidiaNeMoReferencePostprocessorTests
{
    [Fact]
    public void UsesStrictOnsetAndOffsetComparisonsAndKeepsOverlappingSpeakers()
    {
        Assert.Empty(Segment(0, 0.08, 1, 1, [0.5f]));

        IReadOnlyList<DiarizationSegment> segments = Segment(0, 0.08, 1, 2, [0.6f, 0.6f]);
        Assert.Equal(new DiarizationSegment(0, 0.07, 1), segments[0]);
        Assert.Equal(new DiarizationSegment(0, 0.07, 2), segments[1]);

        // A probability equal to offset does not close speech; the following value below it does.
        segments = Segment(0, 0.08, 3, 1, [0.6f, 0.5f, 0.4f]);
        Assert.Equal(new DiarizationSegment(0, 0.16, 1), Assert.Single(segments));
    }

    [Fact]
    public void UsesFinalFrameConventionAbsoluteOffsetsPaddingAndRounding()
    {
        IReadOnlyList<DiarizationSegment> finalActive = Segment(0, 0.08, 2, 1, [0.1f, 0.6f]);
        Assert.Equal(new DiarizationSegment(0.08, 0.15, 1), Assert.Single(finalActive));

        var snapshot = new DiarizationSnapshot(10, 11, 1, 0.08, [0.6f], [], true);
        IReadOnlyList<DiarizationSegment> offset = NvidiaNeMoReferencePostprocessor.Segment(snapshot,
            new DiarizationPostprocessingOptions { PadOnsetSeconds = 0.02, PadOffsetSeconds = 0.02 });
        Assert.Equal(new DiarizationSegment(0.8, 0.89, 1), Assert.Single(offset));
    }

    [Fact]
    public void AppliesMinimumSpeechFilterBeforeShortGapMergeAndSupportsTenMillisecondFrames()
    {
        IReadOnlyList<DiarizationSegment> segments = Segment(0, 0.08, 3, 1,
            [0.6f, 0.1f, 0.6f], new DiarizationPostprocessingOptions
            {
                MinDurationSeconds = 0.075,
                MinGapSeconds = 0.09
            });
        Assert.Equal(new DiarizationSegment(0, 0.08, 1), Assert.Single(segments));

        // At a 10 ms cadence, NeMo's final-frame end convention yields zero duration for one active frame.
        var singleFrame = new DiarizationSnapshot(0, 1, 1, 0.01, [0.6f], [], true);
        Assert.Equal(new DiarizationSegment(0, 0, 1),
            Assert.Single(NvidiaNeMoReferencePostprocessor.Segment(singleFrame)));
    }

    [Theory]
    [InlineData(0.015, 1)]
    [InlineData(0.01, 2)]
    [InlineData(0.005, 2)]
    public void MergesOnlyGapsStrictlyShorterThanTheConfiguredMinimum(double minGap, int expectedCount)
    {
        IReadOnlyList<DiarizationSegment> segments = Segment(0, 0.01, 3, 1,
            [0.6f, 0.1f, 0.6f], new DiarizationPostprocessingOptions { MinGapSeconds = minGap });
        Assert.Equal(expectedCount, segments.Count);
    }

    [Fact]
    public void RejectsCadencesThatCannotMapToTenMillisecondUnits()
    {
        var snapshot = new DiarizationSnapshot(0, 1, 1, 0.015, [0.6f], [], true);
        Assert.Throws<InvalidOperationException>(() => NvidiaNeMoReferencePostprocessor.Segment(snapshot));
    }

    [Fact]
    public void ResultModeSelectsNativeOrReferenceSegmentsWithoutChangingRawProbabilities()
    {
        var nativeSegment = new DiarizationSegment(2, 3, 1);
        var snapshot = new DiarizationSnapshot(0, 1, 1, 0.08, [0.6f], [nativeSegment], true);

        DiarizationSnapshot native = DiarizationStream.ApplyResultOptions(snapshot,
            new DiarizationResultOptions { SegmentationMode = DiarizationSegmentationMode.Native });
        DiarizationSnapshot reference = DiarizationStream.ApplyResultOptions(snapshot,
            new DiarizationResultOptions { SegmentationMode = DiarizationSegmentationMode.NvidiaNeMoReference });

        Assert.Equal(nativeSegment, Assert.Single(native.Segments));
        Assert.Equal(new DiarizationSegment(0, 0.07, 1), Assert.Single(reference.Segments));
        Assert.Equal(snapshot.GetProbability(0, 0), reference.GetProbability(0, 0));
        Assert.Throws<ArgumentException>(() => new DiarizationResultOptions
        {
            SegmentationMode = DiarizationSegmentationMode.Native,
            ManagedOptions = new DiarizationPostprocessingOptions()
        }.Validate());
    }

    [Fact]
    public void FinalReferenceResultRejectsProbabilityHistoryThatStartsAfterFrameZero()
    {
        var snapshot = new DiarizationSnapshot(4, 5, 1, 0.01, [0.8f], [], true);
        Assert.Throws<InvalidOperationException>(() => DiarizationStream.ApplyResultOptions(snapshot,
            new DiarizationResultOptions { SegmentationMode = DiarizationSegmentationMode.NvidiaNeMoReference }));
    }

    [Fact]
    public void AcceptsOwnedProbabilityBatchesForApplicationDefinedReferenceProcessing()
    {
        DiarizationFrameBatch batch = DiarizationFrameBatch.FromProbabilities(2, 1, 0.01, [0.1f, 0.8f, 0.8f]);
        IReadOnlyList<DiarizationSegment> segments = NvidiaNeMoReferencePostprocessor.Segment(batch);
        Assert.Equal(new DiarizationSegment(0.03, 0.04, 1), Assert.Single(segments));
    }

    private static IReadOnlyList<DiarizationSegment> Segment(long firstFrame, double secondsPerFrame,
        int frameCount, int speakerCount, float[] probabilities, DiarizationPostprocessingOptions? options = null)
    {
        var snapshot = new DiarizationSnapshot(firstFrame, firstFrame + frameCount, speakerCount,
            secondsPerFrame, probabilities, [], true);
        return NvidiaNeMoReferencePostprocessor.Segment(snapshot, options);
    }
}
