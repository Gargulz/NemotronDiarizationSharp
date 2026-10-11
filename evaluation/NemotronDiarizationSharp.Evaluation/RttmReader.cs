using System.Globalization;

namespace NemotronDiarizationSharp.Evaluation;

/// <summary>Reads the core fields of SPEAKER records from RTTM text.</summary>
public static class RttmReader
{
    /// <summary>
    /// Reads speaker intervals without taking ownership of <paramref name="reader"/>.
    /// Each non-empty record must contain at least the eight core SPEAKER fields; later metadata fields are ignored.
    /// </summary>
    public static IReadOnlyList<RttmSegment> Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var segments = new List<RttmSegment>();
        string? line;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 8 || !string.Equals(fields[0], "SPEAKER", StringComparison.Ordinal))
                throw InvalidLine(lineNumber, "Expected a SPEAKER record with at least eight core fields.");
            if (!TryParseTimestamp(fields[3], out double startSeconds))
                throw InvalidLine(lineNumber, "Start time must be a finite invariant-culture number.");
            if (!TryParseTimestamp(fields[4], out double durationSeconds))
                throw InvalidLine(lineNumber, "Duration must be a finite invariant-culture number.");
            try
            {
                segments.Add(new RttmSegment(fields[1], fields[2], startSeconds, durationSeconds, fields[7]));
            }
            catch (ArgumentException exception)
            {
                throw InvalidLine(lineNumber, exception.Message, exception);
            }
        }
        return Array.AsReadOnly(segments.ToArray());
    }

    private static bool TryParseTimestamp(string value, out double seconds) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
        && double.IsFinite(seconds);

    private static InvalidDataException InvalidLine(int lineNumber, string message, Exception? innerException = null) =>
        new($"Invalid RTTM at line {lineNumber}: {message}", innerException);
}
