using System.Text.Json;
using NemotronDiarizationSharp.Evaluation;
using Xunit;

namespace NemotronDiarizationSharp.Integration.Tests;

public sealed class ArchivedScoreConsistencyTests
{
    [Fact]
    public void SyntheticArchivedScoreReportMatchesTheSharedCSharpScorer()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic-archived-score.json");
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
        JsonElement root = fixture.RootElement;
        Assert.Equal("synthetic", root.GetProperty("fixtureKind").GetString());
        IReadOnlyList<RttmSegment> reference = RttmReader.Read(new StringReader(root.GetProperty("referenceRttm").GetString()!));
        IReadOnlyList<RttmSegment> hypothesis = RttmReader.Read(new StringReader(root.GetProperty("hypothesisRttm").GetString()!));
        DiarizationErrorRateScore score = DiarizationErrorRateScorer.Score(reference, hypothesis);
        JsonElement expected = root.GetProperty("expected");
        Assert.Equal(expected.GetProperty("der").GetDouble(), score.DiarizationErrorRate);
        Assert.Equal(expected.GetProperty("falseAlarmSpeakerSeconds").GetDouble(), score.FalseAlarmSpeakerSeconds);
        Assert.Equal(expected.GetProperty("missedDetectionSpeakerSeconds").GetDouble(), score.MissedDetectionSpeakerSeconds);
        Assert.Equal(expected.GetProperty("speakerConfusionSpeakerSeconds").GetDouble(), score.SpeakerConfusionSpeakerSeconds);
        Assert.Equal(expected.GetProperty("totalReferenceSpeakerSeconds").GetDouble(), score.TotalReferenceSpeakerSeconds);
        Assert.Equal(expected.GetProperty("correctlyIdentifiedSpeakerSeconds").GetDouble(), score.CorrectlyIdentifiedSpeakerSeconds);
        Assert.Equal(expected.GetProperty("referenceSpeakerCount").GetInt32(), score.ReferenceSpeakerCount);
        Assert.Equal(expected.GetProperty("hypothesisSpeakerCount").GetInt32(), score.HypothesisSpeakerCount);
        Assert.Equal("A", score.SpeakerMapping["X"]);
        Assert.Single(score.SpeakerMapping);
    }
}
