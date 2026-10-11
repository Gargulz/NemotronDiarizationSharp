using NemotronDiarizationSharp.Evaluation;
using Xunit;

namespace NemotronDiarizationSharp.Evaluation.Tests;

public sealed class DiarizationErrorRateTests
{
    [Fact]
    public void PerfectDiarizationHasZeroErrors()
    {
        RttmSegment[] reference = [S(0, 2, "A"), S(3, 1, "B")];
        DiarizationErrorRateScore score = Score(reference, [S(0, 2, "A"), S(3, 1, "B")]);

        AssertScore(score, der: 0, total: 3, correct: 3, falseAlarm: 0, miss: 0, confusion: 0);
        Assert.Equal(2, score.ReferenceSpeakerCount);
        Assert.Equal(2, score.HypothesisSpeakerCount);
        Assert.Equal("A", score.SpeakerMapping["A"]);
        Assert.Equal("B", score.SpeakerMapping["B"]);
    }

    [Fact]
    public void CompleteMissCountsAllReferenceSpeakerTime()
    {
        DiarizationErrorRateScore score = Score([S(0, 2, "A")], []);
        AssertScore(score, der: 1, total: 2, correct: 0, falseAlarm: 0, miss: 2, confusion: 0);
    }

    [Fact]
    public void FalseAlarmWithEmptyReferenceUsesPyannoteZeroDenominatorConventionExplicitly()
    {
        DiarizationErrorRateScore score = Score([], [S(0, 2, "hyp")]);
        AssertScore(score, der: 1, total: 0, correct: 0, falseAlarm: 2, miss: 0, confusion: 0);
        Assert.False(score.HasReferenceSpeech);
    }

    [Fact]
    public void EmptyAnnotationsReportNoReferenceSpeechAndNoMeasuredDenominator()
    {
        DiarizationErrorRateScore score = Score([], []);
        AssertScore(score, der: 0, total: 0, correct: 0, falseAlarm: 0, miss: 0, confusion: 0);
        Assert.False(score.HasReferenceSpeech);
    }

    [Fact]
    public void WrongSpeakerLabelsAreGloballyRelabeledBeforeScoring()
    {
        DiarizationErrorRateScore score = Score([S(0, 2, "ref")], [S(0, 2, "hyp")]);
        AssertScore(score, der: 0, total: 2, correct: 2, falseAlarm: 0, miss: 0, confusion: 0);
        Assert.Equal("ref", score.SpeakerMapping["hyp"]);
    }

    [Fact]
    public void SpeakerLabelPermutationScoresPerfectly()
    {
        DiarizationErrorRateScore score = Score(
            [S(0, 2, "A"), S(3, 2, "B")],
            [S(0, 2, "speaker-2"), S(3, 2, "speaker-1")]);

        AssertScore(score, der: 0, total: 4, correct: 4, falseAlarm: 0, miss: 0, confusion: 0);
        Assert.Equal("B", score.SpeakerMapping["speaker-1"]);
        Assert.Equal("A", score.SpeakerMapping["speaker-2"]);
    }

    [Fact]
    public void SimultaneousSpeakersContributeSeparateSpeakerSecondsToDenominator()
    {
        DiarizationErrorRateScore score = Score(
            [S(0, 2, "A"), S(0, 2, "B")],
            [S(0, 2, "X"), S(0, 2, "Y")]);

        AssertScore(score, der: 0, total: 4, correct: 4, falseAlarm: 0, miss: 0, confusion: 0);
        Assert.Equal(2, score.ReferenceOverlapSeconds);
        Assert.Equal(2, score.HypothesisOverlapSeconds);
    }

    [Fact]
    public void ExtraSimultaneousHypothesisSpeakerIsFalseAlarmSpeakerTime()
    {
        DiarizationErrorRateScore score = Score(
            [S(0, 2, "A")],
            [S(0, 2, "X"), S(0, 2, "Y")]);

        Assert.Equal(2, score.TotalReferenceSpeakerSeconds);
        Assert.Equal(2, score.CorrectlyIdentifiedSpeakerSeconds);
        Assert.Equal(2, score.FalseAlarmSpeakerSeconds);
        Assert.Equal(0, score.MissedDetectionSpeakerSeconds);
        Assert.Equal(0, score.SpeakerConfusionSpeakerSeconds);
        Assert.Equal(1, score.DiarizationErrorRate);
        Assert.Single(score.SpeakerMapping);
    }

    [Fact]
    public void DerIsNotClampedWhenFalseAlarmsExceedReferenceSpeakerTime()
    {
        DiarizationErrorRateScore score = Score(
            [S(0, 2, "A")],
            [S(0, 2, "X"), S(0, 2, "Y"), S(0, 2, "Z")]);

        Assert.Equal(4, score.FalseAlarmSpeakerSeconds);
        Assert.Equal(2, score.TotalReferenceSpeakerSeconds);
        Assert.Equal(2, score.DiarizationErrorRate);
    }

    [Fact]
    public void MissingSimultaneousHypothesisSpeakerIsMissedSpeakerTime()
    {
        DiarizationErrorRateScore score = Score(
            [S(0, 2, "A"), S(0, 2, "B")],
            [S(0, 2, "X")]);

        Assert.Equal(4, score.TotalReferenceSpeakerSeconds);
        Assert.Equal(2, score.CorrectlyIdentifiedSpeakerSeconds);
        Assert.Equal(0, score.FalseAlarmSpeakerSeconds);
        Assert.Equal(2, score.MissedDetectionSpeakerSeconds);
        Assert.Equal(0, score.SpeakerConfusionSpeakerSeconds);
        Assert.Equal(0.5, score.DiarizationErrorRate);
    }

    [Fact]
    public void MultipleDisconnectedEvaluationWindowsAreSummedWithoutCountingTheGap()
    {
        var options = new DiarizationErrorRateOptions([new(0, 2), new(5, 2)]);
        DiarizationErrorRateScore score = DiarizationErrorRateScorer.Score(
            [S(0, 7, "A")], [S(0, 7, "A")], options);

        AssertScore(score, der: 0, total: 4, correct: 4, falseAlarm: 0, miss: 0, confusion: 0);
    }

    [Fact]
    public void SegmentsCrossingEvaluationBoundariesAreClipped()
    {
        DiarizationErrorRateScore score = DiarizationErrorRateScorer.Score(
            [new RttmSegment("rec", "1", 0, 4, "A").ToRelative(1)],
            [S(0, 2, "X")], new DiarizationErrorRateOptions([new(0, 2)]));

        AssertScore(score, der: 0, total: 2, correct: 2, falseAlarm: 0, miss: 0, confusion: 0);
    }

    [Fact]
    public void DefaultEvaluationWindowEndsAt180Seconds()
    {
        DiarizationErrorRateScore score = Score([S(179, 2, "A")], [S(179, 2, "X")]);
        AssertScore(score, der: 0, total: 1, correct: 1, falseAlarm: 0, miss: 0, confusion: 0);
    }

    [Fact]
    public void BoundaryScoringRetainsSubMillisecondPrecision()
    {
        DiarizationErrorRateScore score = Score([S(0, 0.0000002, "A")],
            [S(0, 0.0000001, "X")]);

        Assert.Equal(0.0000002, score.TotalReferenceSpeakerSeconds, 14);
        Assert.Equal(0.0000001, score.MissedDetectionSpeakerSeconds, 14);
        Assert.Equal(0.5, score.DiarizationErrorRate, 12);
    }

    [Fact]
    public void OverlappingSameSpeakerTracksRetainPyannoteTrackMultiplicity()
    {
        DiarizationErrorRateScore score = Score(
            [S(0, 2, "A"), S(1, 2, "A")],
            [S(0, 3, "X")]);

        AssertScore(score, der: 0.25, total: 4, correct: 3, falseAlarm: 0, miss: 1, confusion: 0);
        Assert.Equal(0, score.ReferenceOverlapSeconds);
    }

    [Fact]
    public void BoundaryTouchingIntervalsDoNotCreateOverlap()
    {
        DiarizationErrorRateScore score = Score(
            [S(0, 1, "A"), S(1, 1, "B")],
            [S(0, 1, "X"), S(1, 1, "Y")]);

        Assert.Equal(0, score.ReferenceOverlapSeconds);
        Assert.Equal(2, score.TotalReferenceSpeakerSeconds);
        Assert.Equal(0, score.DiarizationErrorRate);
    }

    [Fact]
    public void HungarianAssignmentCanBeatGreedyLargestPairFirstAssignment()
    {
        // Co-occurrence matrix: A/X=10, A/Y=9, B/X=9, B/Y=0.
        // Greedy chooses A/X (10); global assignment chooses A/Y + B/X (18).
        RttmSegment[] reference = [S(0, 10, "A"), S(10, 9, "B"), S(19, 9, "A")];
        RttmSegment[] hypothesis = [S(0, 19, "X"), S(19, 9, "Y")];
        DiarizationErrorRateScore score = Score(reference, hypothesis);

        Assert.Equal("B", score.SpeakerMapping["X"]);
        Assert.Equal("A", score.SpeakerMapping["Y"]);
        Assert.Equal(10, score.SpeakerConfusionSpeakerSeconds);
        Assert.Equal(28, score.TotalReferenceSpeakerSeconds);
        Assert.Equal(10d / 28, score.DiarizationErrorRate, 12);
    }

    [Fact]
    public void EqualHungarianTiesMayChooseEitherMappingButKeepSameObjectiveAndComponents()
    {
        RttmSegment[] reference = [S(0, 1, "A"), S(0, 1, "B")];
        RttmSegment[] hypothesis = [S(0, 1, "X"), S(0, 1, "Y")];
        DiarizationErrorRateScore score = Score(reference, hypothesis);

        Assert.Equal(2, score.SpeakerMapping.Count);
        Assert.Equal(2, score.CorrectlyIdentifiedSpeakerSeconds);
        Assert.Equal(0, score.SpeakerConfusionSpeakerSeconds);
        Assert.Equal(0, score.DiarizationErrorRate);
    }

    [Fact]
    public void ResultsDoNotDependOnRttmRecordOrder()
    {
        RttmSegment[] reference = [S(0, 1, "A"), S(0, 1, "B"), S(2, 1, "C")];
        RttmSegment[] hypothesis = [S(0, 1, "X"), S(0, 1, "Y"), S(2, 1, "Z")];
        DiarizationErrorRateScore forward = Score(reference, hypothesis);
        DiarizationErrorRateScore reverse = Score(reference.Reverse(), hypothesis.Reverse());

        Assert.Equal(forward.TotalReferenceSpeakerSeconds, reverse.TotalReferenceSpeakerSeconds);
        Assert.Equal(forward.CorrectlyIdentifiedSpeakerSeconds, reverse.CorrectlyIdentifiedSpeakerSeconds);
        Assert.Equal(forward.FalseAlarmSpeakerSeconds, reverse.FalseAlarmSpeakerSeconds);
        Assert.Equal(forward.MissedDetectionSpeakerSeconds, reverse.MissedDetectionSpeakerSeconds);
        Assert.Equal(forward.SpeakerConfusionSpeakerSeconds, reverse.SpeakerConfusionSpeakerSeconds);
        Assert.Equal(forward.DiarizationErrorRate, reverse.DiarizationErrorRate);
        Assert.Equal(forward.SpeakerMapping.OrderBy(pair => pair.Key),
            reverse.SpeakerMapping.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void ScoreMappingIsImmutable()
    {
        DiarizationErrorRateScore score = Score([S(0, 1, "A")], [S(0, 1, "X")]);
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(score.SpeakerMapping);
        Assert.Equal("A", score.SpeakerMapping["X"]);
    }

    [Fact]
    public void RejectsMismatchedRecordingIds()
    {
        Assert.Throws<ArgumentException>(() => DiarizationErrorRateScorer.Score(
            [new RttmSegment("reference", "1", 0, 1, "A")],
            [new RttmSegment("hypothesis", "1", 0, 1, "X")]));
    }

    [Fact]
    public void RejectsOverlappingEvaluationWindows()
    {
        Assert.Throws<ArgumentException>(() => new DiarizationErrorRateOptions([new(0, 2), new(1, 2)]));
    }

    [Fact]
    public void RttmSegmentsRejectZeroDurationBeforeScoring()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RttmSegment("rec", "1", 0, 0, "A"));
    }

    private static DiarizationErrorRateScore Score(
        IEnumerable<RttmSegment> reference,
        IEnumerable<RttmSegment> hypothesis) =>
        DiarizationErrorRateScorer.Score(reference, hypothesis);

    private static RttmSegment S(double start, double duration, string speaker) =>
        new("rec", "1", start, duration, speaker);

    private static void AssertScore(
        DiarizationErrorRateScore score,
        double der,
        double total,
        double correct,
        double falseAlarm,
        double miss,
        double confusion)
    {
        Assert.Equal(der, score.DiarizationErrorRate, 12);
        Assert.Equal(total, score.TotalReferenceSpeakerSeconds, 12);
        Assert.Equal(correct, score.CorrectlyIdentifiedSpeakerSeconds, 12);
        Assert.Equal(falseAlarm, score.FalseAlarmSpeakerSeconds, 12);
        Assert.Equal(miss, score.MissedDetectionSpeakerSeconds, 12);
        Assert.Equal(confusion, score.SpeakerConfusionSpeakerSeconds, 12);
    }
}
