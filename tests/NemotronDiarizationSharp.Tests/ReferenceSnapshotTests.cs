using System.Buffers.Binary;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace NemotronDiarizationSharp.Tests;

public sealed class ReferenceSnapshotTests
{
    [Fact]
    public void ReprocessesPreservedProbabilitySnapshots()
    {
        string? snapshotDirectory = FindSnapshotDirectory();
        if (snapshotDirectory is null)
            throw SkipException.ForSkip("Reference snapshots are unavailable; set NEMOTONDIARIZATIONSHARP_REFERENCE_SNAPSHOT_DIR to the preserved runs directory.");

        string[] runs = ["TS3003a-C0", "TS3003a-C1-authorized-correction-03", "EN2002a-C0", "EN2002a-C1"];
        var expectedSegmentCounts = new Dictionary<string, int>
        {
            ["TS3003a-C0"] = 77,
            ["TS3003a-C1-authorized-correction-03"] = 75,
            ["EN2002a-C0"] = 166,
            ["EN2002a-C1"] = 174
        };
        foreach (string run in runs)
        {
            string runPath = Path.Combine(snapshotDirectory, run);
            string metadataPath = Path.Combine(runPath, "metadata.json");
            string probabilityPath = Path.Combine(runPath, "probabilities.f32le");
            if (!File.Exists(metadataPath) || !File.Exists(probabilityPath))
                throw SkipException.ForSkip($"Preserved reference snapshot {run} is not available in this checkout.");

            using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
            JsonElement snapshotInfo = metadata.RootElement.GetProperty("snapshot");
            int speakers = snapshotInfo.GetProperty("speakerCount").GetInt32();
            int frames = snapshotInfo.GetProperty("frameCount").GetInt32();
            double secondsPerFrame = snapshotInfo.GetProperty("secondsPerFrame").GetDouble();
            byte[] bytes = File.ReadAllBytes(probabilityPath);
            Assert.Equal(checked(frames * speakers * sizeof(float)), bytes.Length);
            var probabilities = new float[frames * speakers];
            for (int i = 0; i < probabilities.Length; i++)
                probabilities[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * sizeof(float))));

            DiarizationFrameBatch batch = DiarizationFrameBatch.FromProbabilities(0, speakers,
                secondsPerFrame, probabilities);
            IReadOnlyList<DiarizationSegment> segments = NvidiaNeMoReferencePostprocessor.Segment(batch);
            Assert.Equal(18_001, frames);
            Assert.Equal(8, speakers);
            Assert.Equal(expectedSegmentCounts[run], segments.Count);
        }
    }

    private static string? FindSnapshotDirectory()
    {
        string? path = Environment.GetEnvironmentVariable("NEMOTONDIARIZATIONSHARP_REFERENCE_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("NEMOTONDIARIZATIONSHARP_REFERENCE_SNAPSHOT_DIR must be an absolute directory.");
        return Directory.Exists(path) ? path : null;
    }
}
