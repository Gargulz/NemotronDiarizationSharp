using System.Collections.ObjectModel;

namespace NemotronDiarizationSharp.Evaluation;

/// <summary>Immutable DER components and speaker assignment for one evaluated recording.</summary>
public sealed class DiarizationErrorRateScore
{
    internal DiarizationErrorRateScore(
        double falseAlarmSpeakerSeconds,
        double missedDetectionSpeakerSeconds,
        double speakerConfusionSpeakerSeconds,
        double totalReferenceSpeakerSeconds,
        double correctlyIdentifiedSpeakerSeconds,
        int referenceSpeakerCount,
        int hypothesisSpeakerCount,
        double referenceOverlapSeconds,
        double hypothesisOverlapSeconds,
        IDictionary<string, string> speakerMapping)
    {
        FalseAlarmSpeakerSeconds = falseAlarmSpeakerSeconds;
        MissedDetectionSpeakerSeconds = missedDetectionSpeakerSeconds;
        SpeakerConfusionSpeakerSeconds = speakerConfusionSpeakerSeconds;
        TotalReferenceSpeakerSeconds = totalReferenceSpeakerSeconds;
        CorrectlyIdentifiedSpeakerSeconds = correctlyIdentifiedSpeakerSeconds;
        ReferenceSpeakerCount = referenceSpeakerCount;
        HypothesisSpeakerCount = hypothesisSpeakerCount;
        ReferenceOverlapSeconds = referenceOverlapSeconds;
        HypothesisOverlapSeconds = hypothesisOverlapSeconds;
        SpeakerMapping = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(speakerMapping, StringComparer.Ordinal));

        double errors = FalseAlarmSpeakerSeconds + MissedDetectionSpeakerSeconds + SpeakerConfusionSpeakerSeconds;
        // Match pyannote.metrics 4.1 for a zero denominator, while exposing that
        // case through HasReferenceSpeech so a zero cannot be mistaken for a measured DER.
        DiarizationErrorRate = totalReferenceSpeakerSeconds == 0
            ? errors == 0 ? 0 : 1
            : errors / totalReferenceSpeakerSeconds;
    }

    /// <summary>Gets the sum of false-alarm hypothesis speaker time, in seconds.</summary>
    public double FalseAlarmSpeakerSeconds { get; }

    /// <summary>Gets the sum of missed reference speaker time, in seconds.</summary>
    public double MissedDetectionSpeakerSeconds { get; }

    /// <summary>Gets the sum of confusion speaker time, in seconds.</summary>
    public double SpeakerConfusionSpeakerSeconds { get; }

    /// <summary>Gets the total reference speaker time. Overlapping speakers each contribute their own time.</summary>
    public double TotalReferenceSpeakerSeconds { get; }

    /// <summary>Gets the reference speaker time correctly matched after global assignment.</summary>
    public double CorrectlyIdentifiedSpeakerSeconds { get; }

    /// <summary>Gets whether the evaluated reference contains any speaker time.</summary>
    public bool HasReferenceSpeech => TotalReferenceSpeakerSeconds > 0;

    /// <summary>Gets DER as errors divided by reference speaker time, using pyannote's zero-denominator convention.</summary>
    public double DiarizationErrorRate { get; }

    /// <summary>Gets the number of reference speakers present in the evaluation windows.</summary>
    public int ReferenceSpeakerCount { get; }

    /// <summary>Gets the number of hypothesis speakers present in the evaluation windows.</summary>
    public int HypothesisSpeakerCount { get; }

    /// <summary>Gets wall-clock time with two or more distinct reference speakers active.</summary>
    public double ReferenceOverlapSeconds { get; }

    /// <summary>Gets wall-clock time with two or more distinct hypothesis speakers active.</summary>
    public double HypothesisOverlapSeconds { get; }

    /// <summary>Gets positive-overlap mappings from hypothesis speaker labels to reference labels.</summary>
    public IReadOnlyDictionary<string, string> SpeakerMapping { get; }
}
