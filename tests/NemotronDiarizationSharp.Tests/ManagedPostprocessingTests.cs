using System.Globalization;
using NemotronDiarizationSharp;
using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class ManagedPostprocessingTests
{
    [Fact]
    public void ManagedOptionsHaveExplicitDefaultsAndAllowZeros()
    {
        var defaults = new DiarizationPostprocessingOptions();
        Assert.Equal(0.5f, defaults.Onset);
        Assert.Equal(0.5f, defaults.Offset);
        Assert.Equal(0, defaults.PadOnsetSeconds);
        Assert.Equal(0, defaults.PadOffsetSeconds);
        Assert.Equal(0, defaults.MinGapSeconds);
        Assert.Equal(0, defaults.MinDurationSeconds);
        Assert.Empty(DiarizationPostprocessor.Segment(Batch(0, 1, 0.1f)));
        Assert.Single(DiarizationPostprocessor.Segment(Batch(0, 1, 0.6f),
            new DiarizationPostprocessingOptions { Onset = 0, Offset = 0 }));
    }

    [Fact]
    public void HysteresisUsesStrictCrossingsAndKeepsOpenWindowSpeech()
    {
        DiarizationFrameBatch batch = Batch(0, 1, 0.5f, 0.8f, 0.5f, 0.2f);
        IReadOnlyList<DiarizationSegment> segments = DiarizationPostprocessor.Segment(batch,
            new DiarizationPostprocessingOptions { Onset = 0.5f, Offset = 0.2f });
        DiarizationSegment segment = Assert.Single(segments);
        Assert.Equal(0.1, segment.StartTime, 12);
        Assert.Equal(0.4, segment.EndTime, 12);
        Assert.Equal(1, segment.Speaker);
    }

    [Fact]
    public void SegmentationPreservesOverlapAndUsesAbsoluteTailTiming()
    {
        DiarizationFrameBatch batch = DiarizationFrameBatch.FromProbabilities(10, 2, 0.1,
            [0.9f, 0.1f, 0.9f, 0.9f, 0.1f, 0.9f]);
        IReadOnlyList<DiarizationSegment> segments = DiarizationPostprocessor.Segment(batch);
        Assert.Equal(2, segments.Count);
        Assert.Equal(1.0, segments[0].StartTime, 12);
        Assert.Equal(1.2, segments[0].EndTime, 12);
        Assert.Equal(1, segments[0].Speaker);
        Assert.Equal(1.1, segments[1].StartTime, 12);
        Assert.Equal(1.3, segments[1].EndTime, 12);
        Assert.Equal(2, segments[1].Speaker);
    }

    [Fact]
    public void PaddingClipsToWindowAndGapMergePrecedesDurationFilter()
    {
        DiarizationFrameBatch batch = Batch(5, 1, 0.9f, 0.1f, 0.9f);
        IReadOnlyList<DiarizationSegment> merged = DiarizationPostprocessor.Segment(batch,
            new DiarizationPostprocessingOptions
            {
                PadOnsetSeconds = 1,
                PadOffsetSeconds = 1,
                MinGapSeconds = 0.1,
                MinDurationSeconds = 0.3
            });
        Assert.Equal(new DiarizationSegment(0.5, 0.8, 1), Assert.Single(merged));
        Assert.Empty(DiarizationPostprocessor.Segment(batch,
            new DiarizationPostprocessingOptions { MinGapSeconds = 0.099, MinDurationSeconds = 0.2 }));
    }

    [Fact]
    public void PaddingRunsThatOverlapAreMergedBeforeDurationFiltering()
    {
        DiarizationFrameBatch batch = DiarizationFrameBatch.FromProbabilities(0, 1, 1,
            [0.9f, 0.1f, 0.9f]);
        IReadOnlyList<DiarizationSegment> segments = DiarizationPostprocessor.Segment(batch,
            new DiarizationPostprocessingOptions
            {
                PadOnsetSeconds = 0.75,
                PadOffsetSeconds = 0.75,
                MinGapSeconds = 0
            });
        Assert.Equal(new DiarizationSegment(0, 3, 1), Assert.Single(segments));
    }

    [Fact]
    public void RejectsInvalidSettingsAndNonfiniteAbsoluteTiming()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationPostprocessor.Segment(Batch(0, 1, 0.8f),
            new DiarizationPostprocessingOptions { Offset = 0.8f, Onset = 0.7f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationPostprocessor.Segment(Batch(0, 1, 0.8f),
            new DiarizationPostprocessingOptions { PadOffsetSeconds = double.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationPostprocessor.Segment(
            DiarizationFrameBatch.FromProbabilities(1, 1, double.MaxValue, [0.8f])));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationPostprocessor.Segment(
            DiarizationFrameBatch.FromProbabilities(8, 1, 1e307, [0.8f]),
            new DiarizationPostprocessingOptions { PadOffsetSeconds = double.MaxValue }));
    }

    [Fact]
    public void BoundedHistoryEvictsAndReturnsOwnedCopies()
    {
        var history = new DiarizationProbabilityHistory(2);
        history.Append(Batch(0, 1, 0.1f, 0.2f));
        DiarizationFrameBatch owned = history.GetFrames();
        history.Append(Batch(2, 1, 0.3f, 0.4f));
        Assert.Equal(2, history.FirstRetainedFrame);
        Assert.Equal(4, history.EndFrameExclusive);
        Assert.False(history.IsCompleteFromFrameZero);
        Assert.Equal(new[] { 0.1f, 0.2f }, owned.Probabilities);
        Assert.Equal(new[] { 0.3f, 0.4f }, history.GetFrames().Probabilities);
    }

    [Fact]
    public void FullHistoryRetainsAllFramesAndCanBeRestoredFromTail()
    {
        var full = new DiarizationProbabilityHistory(null);
        full.Append(Batch(0, 1, 0.1f));
        full.Append(Batch(1, 1, 0.2f));
        Assert.True(full.IsCompleteFromFrameZero);
        Assert.Equal(2, full.RetainedFrameCount);

        var tail = new DiarizationProbabilityHistory(10);
        tail.Append(Batch(8, 1, 0.8f));
        Assert.False(tail.IsCompleteFromFrameZero);
        Assert.Equal(8, tail.GetFrames().FirstFrameIndex);
    }

    [Fact]
    public void ApplyReplacesRetainedRevisionsAndIgnoresDeliberatelyEvictedFrames()
    {
        var history = new DiarizationProbabilityHistory(2);
        Guid source = Guid.NewGuid();
        history.Apply(Update(source, 3, 0, 3, Batch(0, 1, 0.1f, 0.2f, 0.3f)));
        history.Apply(Update(source, 4, 0, 3, Batch(3, 1),
            [Batch(0, 1, 0.9f, 0.2f), Batch(2, 1, 0.8f)]));
        Assert.Equal(1, history.FirstRetainedFrame);
        Assert.Equal(new[] { 0.2f, 0.8f }, history.GetFrames().Probabilities);
    }

    [Fact]
    public void BoundedMultiSpeakerHistorySurvivesGrowthWrapAndRevision()
    {
        var history = new DiarizationProbabilityHistory(5);
        Guid source = Guid.NewGuid();
        history.Apply(Update(source, 1, 0, 2, Batch(0, 2, 0.1f, 0.11f, 0.2f, 0.22f)));
        history.Apply(Update(source, 2, 0, 4, Batch(2, 2, 0.3f, 0.33f, 0.4f, 0.44f)));
        history.Apply(Update(source, 3, 0, 6, Batch(4, 2, 0.5f, 0.55f, 0.6f, 0.66f)));
        history.Apply(Update(source, 4, 1, 6, Batch(6, 2), [Batch(1, 2, 0.9f, 0.99f)]));

        Assert.Equal(1, history.FirstRetainedFrame);
        Assert.Equal(new[] { 0.9f, 0.99f, 0.3f, 0.33f, 0.4f, 0.44f, 0.5f, 0.55f, 0.6f, 0.66f },
            history.GetFrames().Probabilities);
    }

    [Fact]
    public void ApplyRejectsBadPacketsTransactionally()
    {
        Guid source = Guid.NewGuid();
        var history = new DiarizationProbabilityHistory(null);
        history.Apply(Update(source, 5, 0, 2, Batch(0, 1, 0.1f, 0.2f)));
        DiarizationFrameBatch before = history.GetFrames();

        Assert.Throws<InvalidOperationException>(() => history.Apply(Update(source, 6, 2, 4,
            Batch(2, 1, 0.3f, 0.4f), historyGap: new DiarizationFrameRange(1, 2))));
        Assert.Throws<InvalidOperationException>(() => history.Apply(Update(source, 7, 0, 2,
            Batch(2, 1), [Batch(0, 1, 0.9f)]))); // skipped revision-only sequence
        Assert.Throws<InvalidOperationException>(() => history.Apply(Update(Guid.NewGuid(), 6, 0, 2,
            Batch(2, 1))));
        Assert.Throws<InvalidOperationException>(() => history.Apply(Update(source, 6, 0, 3,
            Batch(3, 1))));
        var uncoveredRevision = new DiarizationResultUpdate(source, 6, 0, 2, 1, 0.1, false,
            Batch(2, 1), [Batch(0, 1, 0.9f), Batch(1, 1, 0.8f)], [], null,
            new DiarizationFrameRange(0, 1));
        Assert.Throws<InvalidOperationException>(() => history.Apply(uncoveredRevision));
        var mismatchedRevision = new DiarizationResultUpdate(source, 6, 0, 2, 1, 0.1, false,
            Batch(2, 1), [Batch(0, 1, 0.9f), Batch(1, 2, 0.8f, 0.7f)], [], null,
            new DiarizationFrameRange(0, 2));
        Assert.Throws<InvalidOperationException>(() => history.Apply(mismatchedRevision));
        var reversedCoverage = new DiarizationResultUpdate(source, 6, 0, 2, 1, 0.1, false,
            Batch(2, 1), [], [], null, new DiarizationFrameRange(1, 1));
        Assert.Throws<InvalidOperationException>(() => history.Apply(reversedCoverage));
        var coveragePastWindow = new DiarizationResultUpdate(source, 6, 0, 3, 1, 0.1, false,
            Batch(2, 1, 0.3f), [], [], null,
            new DiarizationFrameRange(0, 4));
        Assert.Throws<InvalidOperationException>(() => history.Apply(coveragePastWindow));
        var coverageIntoNewFrames = new DiarizationResultUpdate(source, 6, 0, 3, 1, 0.1, false,
            Batch(2, 1, 0.3f), [], [], null,
            new DiarizationFrameRange(1, 3));
        Assert.Throws<InvalidOperationException>(() => history.Apply(coverageIntoNewFrames));
        Assert.Equal(before.Probabilities, history.GetFrames().Probabilities);
        Assert.Equal(2, history.EndFrameExclusive);
    }

    [Fact]
    public void AppendRequiresContiguousMatchingMetadataAndPreservesStateOnFailure()
    {
        var history = new DiarizationProbabilityHistory(2);
        history.Append(Batch(10, 1, 0.1f, 0.2f));
        Assert.Throws<InvalidOperationException>(() => history.Append(Batch(13, 1, 0.3f)));
        Assert.Throws<InvalidOperationException>(() => history.Append(Batch(12, 2, 0.3f, 0.4f)));
        Assert.Equal(new[] { 0.1f, 0.2f }, history.GetFrames().Probabilities);
        history.Append(Batch(12, 1, 0.3f));
        Assert.Equal(new[] { 0.2f, 0.3f }, history.GetFrames().Probabilities);
    }

    [Fact]
    public void EmptyAppendDoesNotInventCoverageAndLiveAttachmentRequiresExactEnd()
    {
        var empty = new DiarizationProbabilityHistory(4);
        empty.Append(Batch(30, 1));
        Assert.Equal(0, empty.EndFrameExclusive);
        Assert.Throws<InvalidOperationException>(() => empty.GetFrames());
        empty.Append(Batch(0, 1, 0.1f));

        var restored = new DiarizationProbabilityHistory(null);
        restored.Append(Batch(5, 1, 0.5f));
        Assert.Throws<InvalidOperationException>(() => restored.Apply(Update(Guid.NewGuid(), 1, 6, 8, Batch(7, 1, 0.6f))));
        restored.Apply(Update(Guid.NewGuid(), 2, 0, 7, Batch(6, 1, 0.6f)));
        Assert.True(restored.IsBoundToSource);
    }

    [Fact]
    public void RttmUsesInvariantFormattingPreservesOverlapAndRejectsInvalidInput()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            using var writer = new StringWriter();
            DiarizationPostprocessor.WriteRttm(writer,
                [new DiarizationSegment(1.25, 2.5, 1), new DiarizationSegment(1.5, 3, 2)], "rec", 2);
            string[] lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Equal("SPEAKER rec 2 1.25 1.25 <NA> <NA> speaker_1 <NA> <NA>", lines[0]);
            Assert.Equal("SPEAKER rec 2 1.5 1.5 <NA> <NA> speaker_2 <NA> <NA>", lines[1]);
            Assert.Throws<ArgumentException>(() => DiarizationPostprocessor.WriteRttm(TextWriter.Null,
                [new DiarizationSegment(0, 0, 1)], "rec"));
            Assert.Throws<ArgumentException>(() => DiarizationPostprocessor.WriteRttm(TextWriter.Null, [], "two words"));
            Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationPostprocessor.WriteRttm(TextWriter.Null, [], "rec", 0));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static DiarizationFrameBatch Batch(long first, int speakers, params float[] values) =>
        DiarizationFrameBatch.FromProbabilities(first, speakers, 0.1, values);

    private static DiarizationResultUpdate Update(Guid source, long sequence, long firstRetained, long total,
        DiarizationFrameBatch added, DiarizationFrameBatch[]? revisions = null,
        DiarizationFrameRange? historyGap = null)
    {
        DiarizationFrameRange? compared = revisions is { Length: > 0 }
            ? new DiarizationFrameRange(revisions.Min(batch => batch.FirstFrameIndex),
                revisions.Max(batch => checked(batch.FirstFrameIndex + batch.FrameCount)))
            : null;
        return
        new(source, sequence, firstRetained, total, added.SpeakerCount, added.SecondsPerFrame, false,
            added, revisions ?? [], [], historyGap, compared);
    }
}
