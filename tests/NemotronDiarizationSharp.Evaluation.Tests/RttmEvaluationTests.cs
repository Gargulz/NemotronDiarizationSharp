using System.Globalization;
using NemotronDiarizationSharp.Evaluation;
using Xunit;

namespace NemotronDiarizationSharp.Evaluation.Tests;

public sealed class RttmEvaluationTests
{
    [Fact]
    public void ReaderParsesSpeakerRecordsAndRetainsRecordingChannelAndLabels()
    {
        const string text = "SPEAKER meeting-A 1 1.250000 2.500000 <NA> <NA> speaker-A <NA> <NA>\n"
            + "SPEAKER meeting-A 2 2.000000 0.500000 <NA> <NA> speaker-B <NA> <NA>\n";
        IReadOnlyList<RttmSegment> segments = RttmReader.Read(new StringReader(text));
        Assert.Collection(segments,
            first =>
            {
                Assert.Equal("meeting-A", first.RecordingId);
                Assert.Equal("1", first.ChannelId);
                Assert.Equal(1.25, first.StartSeconds);
                Assert.Equal(2.5, first.DurationSeconds);
                Assert.Equal("speaker-A", first.SpeakerLabel);
            },
            second =>
            {
                Assert.Equal("meeting-A", second.RecordingId);
                Assert.Equal("2", second.ChannelId);
                Assert.Equal("speaker-B", second.SpeakerLabel);
            });
    }

    [Fact]
    public void ReaderUsesInvariantCulture()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            RttmSegment segment = Assert.Single(RttmReader.Read(new StringReader(
                "SPEAKER rec 1 12.375 0.625 <NA> <NA> spk <NA> <NA>")));
            Assert.Equal(12.375, segment.StartSeconds);
            Assert.Equal(0.625, segment.DurationSeconds);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void ReaderAcceptsEightCoreFieldsAndIgnoresAdditionalMetadata()
    {
        RttmSegment compact = Assert.Single(RttmReader.Read(new StringReader(
            "SPEAKER rec 1 0.5 1.25 <NA> <NA> spk")));
        RttmSegment extended = Assert.Single(RttmReader.Read(new StringReader(
            "SPEAKER rec 1 0.5 1.25 <NA> <NA> spk <NA> <NA> extra")));
        Assert.Equal(compact, extended);
    }

    [Theory]
    [InlineData(0.0, 2.0)]
    [InlineData(4.0, 2.0)]
    [InlineData(8.0, 2.0)]
    public void ReaderAcceptsNonnegativeBoundaryTimes(double start, double duration)
    {
        string line = FormattableString.Invariant($"SPEAKER rec 1 {start} {duration} <NA> <NA> spk <NA> <NA>");
        RttmSegment segment = Assert.Single(RttmReader.Read(new StringReader(line)));
        Assert.Equal(start, segment.StartSeconds);
        Assert.Equal(duration, segment.DurationSeconds);
    }

    [Theory]
    [InlineData("SPEAKER rec 1 0.0 1.0 <NA> <NA>")]
    [InlineData("LEXEME rec 1 0.0 1.0 <NA> <NA> spk <NA>")]
    [InlineData("SPEAKER rec 1 nope 1.0 <NA> <NA> spk <NA>")]
    [InlineData("SPEAKER rec 1 NaN 1.0 <NA> <NA> spk <NA>")]
    [InlineData("SPEAKER rec 1 0.0 Infinity <NA> <NA> spk <NA>")]
    [InlineData("SPEAKER rec 1 -0.1 1.0 <NA> <NA> spk <NA>")]
    [InlineData("SPEAKER rec 1 0.0 0.0 <NA> <NA> spk <NA>")]
    [InlineData("SPEAKER rec 1 0.0 -1.0 <NA> <NA> spk <NA>")]
    [InlineData("SPEAKER rec 1 1.0E308 1.0E308 <NA> <NA> spk <NA>")]
    public void ReaderRejectsInvalidOrMissingFields(string line)
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            RttmReader.Read(new StringReader(line)));
        Assert.Contains("line 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReaderReportsLineNumberAndSkipsBlankLines()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            RttmReader.Read(new StringReader("\nSPEAKER rec 1 bad 1 <NA> <NA> spk <NA>")));
        Assert.Contains("line 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClipReturnsRelativeIntersectionAndDropsDisjointIntervals()
    {
        var window = new RttmTimeWindow(10, 5);
        var before = new RttmSegment("rec", "1", 8, 4, "spk-A");
        var inside = new RttmSegment("rec", "1", 11, 1, "spk-B");
        var after = new RttmSegment("rec", "1", 16, 1, "spk-C");
        RttmSegment clippedBefore = Assert.IsType<RttmSegment>(window.Clip(before));
        RttmSegment clippedInside = Assert.IsType<RttmSegment>(window.Clip(inside));
        Assert.Equal(0, clippedBefore.StartSeconds);
        Assert.Equal(2, clippedBefore.DurationSeconds);
        Assert.Equal(1, clippedInside.StartSeconds);
        Assert.Equal(1, clippedInside.DurationSeconds);
        Assert.Null(window.Clip(after));
    }

    [Fact]
    public void RelativeAndAbsoluteConversionRoundTrips()
    {
        var absolute = new RttmSegment("rec", "1", 12.25, 0.75, "spk");
        RttmSegment relative = absolute.ToRelative(10);
        RttmSegment restored = relative.ToAbsolute(10);
        Assert.Equal(2.25, relative.StartSeconds);
        Assert.Equal(absolute, restored);
    }

    [Fact]
    public void RelativeConversionCanRepresentAnIntervalBeforeItsOrigin()
    {
        RttmSegment relative = new RttmSegment("rec", "1", 2, 1, "spk").ToRelative(3);
        Assert.Equal(-1, relative.StartSeconds);
        Assert.Throws<ArgumentException>(() => RttmWriter.Write(new StringWriter(), [relative]));
    }

    [Fact]
    public void WriterRejectsDurationRoundedToZeroAtItsPrecision()
    {
        using var writer = new StringWriter();
        Assert.Throws<ArgumentException>(() => RttmWriter.Write(writer,
            [new RttmSegment("rec", "1", 0, 0.0000001, "spk")]));
    }

    [Fact]
    public void WriterUsesInvariantSixDecimalTimestampsAndStableOrdering()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            RttmSegment[] segments =
            [
                new("rec", "1", 2, 0.5, "speaker-B"),
                new("rec", "1", 1.125, 0.25, "speaker-A"),
                new("rec", "1", 2, 0.5, "speaker-A")
            ];
            using var writer = new StringWriter();
            RttmWriter.Write(writer, segments);
            Assert.Equal(
                "SPEAKER rec 1 1.125000 0.250000 <NA> <NA> speaker-A <NA> <NA>\n"
                    + "SPEAKER rec 1 2.000000 0.500000 <NA> <NA> speaker-A <NA> <NA>\n"
                    + "SPEAKER rec 1 2.000000 0.500000 <NA> <NA> speaker-B <NA> <NA>\n",
                NormalizeNewLines(writer.ToString()));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void WriterOrdersSameKeySegmentsByDurationAndRoundsToSixDecimals()
    {
        RttmSegment longer = new("rec", "1", 1, 0.5000004, "spk");
        RttmSegment shorter = new("rec", "1", 1, 0.2500004, "spk");
        using var forwardWriter = new StringWriter(CultureInfo.InvariantCulture);
        using var reverseWriter = new StringWriter(CultureInfo.InvariantCulture);
        RttmWriter.Write(forwardWriter, [longer, shorter]);
        RttmWriter.Write(reverseWriter, [shorter, longer]);
        Assert.Equal(forwardWriter.ToString(), reverseWriter.ToString());
        Assert.Equal(
            "SPEAKER rec 1 1.000000 0.250000 <NA> <NA> spk <NA> <NA>\n"
                + "SPEAKER rec 1 1.000000 0.500000 <NA> <NA> spk <NA> <NA>\n",
            NormalizeNewLines(forwardWriter.ToString()));
        Assert.Equal(new RttmSegment("rec", "1", 1, 0.25, "spk"),
            RttmReader.Read(new StringReader(forwardWriter.ToString())).First());
    }

    [Fact]
    public void WriterAndReaderRoundTripAllSpeakerIntervalsIncludingOverlap()
    {
        RttmSegment[] source =
        [
            new("meeting", "1", 1.125001, 2.0, "A"),
            new("meeting", "1", 2.0, 0.5, "B")
        ];
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        RttmWriter.Write(writer, source);
        IReadOnlyList<RttmSegment> restored = RttmReader.Read(new StringReader(writer.ToString()));
        Assert.Equal(2, restored.Count);
        Assert.Equal(source[0], restored[0]);
        Assert.Equal(source[1], restored[1]);
        Assert.True(restored[1].StartSeconds < restored[0].EndSeconds);
    }

    [Fact]
    public void EmptyInputProducesAnEmptySegmentListAndEmptyOutput()
    {
        Assert.Empty(RttmReader.Read(new StringReader("\n  \n")));
        using var writer = new StringWriter();
        RttmWriter.Write(writer, Array.Empty<RttmSegment>());
        Assert.Equal(string.Empty, writer.ToString());
    }

    [Theory]
    [InlineData(null, "1", 0, 1, "spk")]
    [InlineData("rec with-space", "1", 0, 1, "spk")]
    [InlineData("rec", "", 0, 1, "spk")]
    [InlineData("rec", "1", -1, 1, "spk")]
    [InlineData("rec", "1", 0, 0, "spk")]
    [InlineData("rec", "1", 0, -1, "spk")]
    [InlineData("rec", "1", 0, 1, "speaker label")]
    public void SegmentConstructorValidatesInput(string? recording, string channel, double start, double duration, string speaker)
    {
        Assert.ThrowsAny<ArgumentException>(() => new RttmSegment(recording!, channel, start, duration, speaker));
    }

    private static string NormalizeNewLines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
