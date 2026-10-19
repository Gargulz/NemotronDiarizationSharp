using NemotronDiarizationSharp.Native;
using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class DocumentationExamplesTests
{
    [Fact]
    public void DocumentedPresetConstructionPreservesDefaultsAndLeftZero()
    {
        DiarizationModelOptions options = CreateStreamingOptions();
        Assert.Equal(DiarizationPresets.V3Streaming, options.Preset);
        Assert.Equal(DiarizationBackend.Auto, options.Backend);
        Assert.Null(options.GpuDeviceIndex);
        Assert.Null(options.ChunkFrames);
        Assert.Equal(0, options.LeftContextFrames);
        Assert.Equal(-1, options.GetNativeGpuDeviceIndex(DiarizationBackend.Cpu));
        options.ValidateGeometryArithmetic();
        var native = new DiarModelConfig();
        options.ApplyNativeOverrides(ref native);
        Assert.Equal(0, native.ChunkFrames);
        Assert.Equal(0, native.LeftContextFrames);
    }

    [Fact]
    public void DocumentedJsonParsesUsingOnlyManagedValidation()
    {
        DiarizationConfiguration configuration = CreateConfiguration();
        Assert.Equal(DiarizationPresets.V3Streaming, configuration.ModelOptions.Preset);
        Assert.Null(configuration.ModelOptions.GpuDeviceIndex);
        Assert.Null(configuration.ModelOptions.ChunkFrames);
        Assert.Equal(0, configuration.ModelOptions.LeftContextFrames);
        DiarizationSegmentationOptions segmentation = Assert.IsType<DiarizationSegmentationOptions>(
            configuration.SegmentationOptions);
        Assert.Equal(0.64f, segmentation.Onset);
        Assert.Equal(0.4f, segmentation.Offset);
        Assert.Null(segmentation.PadOnsetSeconds);
        Assert.Equal(0.1, segmentation.PadOffsetSeconds);
        Assert.Equal(0, segmentation.MinGapSeconds);
        Assert.Null(segmentation.MinDurationSeconds);
        NativeSegmentationConfig native = segmentation.ToNativeConfig();
        Assert.Equal(0, native.PadOnsetSec);
        Assert.Equal(0.1, native.PadOffsetSec);
        Assert.Equal(0, native.MinGapSec);
        Assert.Equal(0, native.MinDurationSec);
    }

    [Theory]
    [InlineData("docs/en/sdk-guide.md")]
    [InlineData("docs/ru/sdk-guide.md")]
    public void ConsumerExampleMatchesCompiledPublicApiMethod(string readme)
    {
        string root = FindRepositoryRoot();
        string document = File.ReadAllText(Path.Combine(root, readme)).Replace("\r\n", "\n");
        string source = File.ReadAllText(Path.Combine(root, "tests", "NemotronDiarizationSharp.Tests",
            "DocumentationExamplesTests.cs")).Replace("\r\n", "\n");
        string documented = ExtractBetween(document,
            "<!-- sdk-example: consumer-api -->\n```csharp\n", "\n```");
        string compiled = ExtractBetween(source, "// sdk-example: consumer-api\n", "    // sdk-example-end");
        string normalized = string.Join("\n", compiled.TrimEnd('\n').Split('\n')
            .Select(line => line.StartsWith("    ", StringComparison.Ordinal) ? line[4..] : line));
        Assert.Equal(normalized, documented);
    }

    [Theory]
    [InlineData("docs/en/sdk-guide.md", "preset-options")]
    [InlineData("docs/ru/sdk-guide.md", "preset-options")]
    [InlineData("docs/en/sdk-guide.md", "json-configuration")]
    [InlineData("docs/ru/sdk-guide.md", "json-configuration")]
    public void ReadmeExamplesMatchTheNormallyCompiledMethods(string readme, string example)
    {
        string root = FindRepositoryRoot();
        string document = File.ReadAllText(Path.Combine(root, readme)).Replace("\r\n", "\n");
        string source = File.ReadAllText(Path.Combine(root, "tests", "NemotronDiarizationSharp.Tests",
            "DocumentationExamplesTests.cs")).Replace("\r\n", "\n");
        string documented = ExtractBetween(document, "<!-- sdk-example: " + example + " -->\n```csharp\n", "\n```");
        string compiled = ExtractBetween(source, "// sdk-example: " + example + "\n", "    // sdk-example-end");
        string normalized = string.Join("\n", compiled.TrimEnd('\n').Split('\n')
            .Select(line => line.StartsWith("    ", StringComparison.Ordinal) ? line[4..] : line));
        Assert.Equal(normalized, documented);
    }

    [Theory]
    [InlineData("docs/en/sdk-guide.md", "revision-polling")]
    [InlineData("docs/ru/sdk-guide.md", "revision-polling")]
    [InlineData("docs/en/sdk-guide.md", "synthetic-postprocessing")]
    [InlineData("docs/ru/sdk-guide.md", "synthetic-postprocessing")]
    [InlineData("docs/en/sdk-guide.md", "async-results")]
    [InlineData("docs/ru/sdk-guide.md", "async-results")]
    public void RevisionAwareReadmeExamplesMatchTheNormallyCompiledMethods(string readme, string example)
    {
        string root = FindRepositoryRoot();
        string document = File.ReadAllText(Path.Combine(root, readme)).Replace("\r\n", "\n");
        string source = File.ReadAllText(Path.Combine(root, "tests", "NemotronDiarizationSharp.Tests",
            "DocumentationExamplesTests.cs")).Replace("\r\n", "\n");
        string documented = ExtractBetween(document,
            "<!-- sdk-example: " + example + " -->\n```csharp\n", "\n```");
        string compiled = ExtractBetween(source, "// sdk-example: " + example + "\n", "    // sdk-example-end");
        string normalized = string.Join("\n", compiled.TrimEnd('\n').Split('\n')
            .Select(line => line.StartsWith("    ", StringComparison.Ordinal) ? line[4..] : line));
        Assert.Equal(normalized, documented);
    }

    [Fact]
    public void DocumentedSyntheticPostprocessingExampleRunsWithoutNativeInference()
    {
        Assert.Equal("SPEAKER synthetic 1 0 3 <NA> <NA> speaker_1 <NA> <NA>" + Environment.NewLine,
            CreateSyntheticRttm());
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NemotronDiarizationSharp.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Documentation consistency tests require the repository checkout.");
    }

    private static string ExtractBetween(string text, string start, string end)
    {
        int startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, "Missing example marker: " + start);
        startIndex += start.Length;
        int endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= startIndex, "Missing example end marker.");
        return text[startIndex..endIndex];
    }

    // sdk-example: preset-options
    static DiarizationModelOptions CreateStreamingOptions()
    {
        return new DiarizationModelOptions
        {
            GpuDeviceIndex = null,
            Preset = DiarizationPresets.V3Streaming,
            LeftContextFrames = 0,
            ChunkFrames = null
        };
    }
    // sdk-example-end

    // sdk-example: json-configuration
    static DiarizationConfiguration CreateConfiguration()
    {
        return DiarizationConfiguration.Parse("""
            {
              "modelOptions": {
                "gpuDeviceIndex": null,
                "preset": "v3-streaming",
                "leftContextFrames": 0,
                "chunkFrames": null
              },
              "segmentationOptions": {
                "onset": 0.64,
                "offset": 0.4,
                "padOnsetSeconds": null,
                "padOffsetSeconds": 0.1,
                "minGapSeconds": 0,
                "minDurationSeconds": null
              }
            }
            """);
    }

    static DiarizationSnapshot ProcessConfiguredStream(string modelPath, float[] samples, int sampleRate)
    {
        DiarizationConfiguration configuration = CreateConfiguration();
        using var model = DiarizationModel.Load(modelPath, configuration.ModelOptions);
        using var stream = model.OpenStream();
        stream.Push(samples, sampleRate);
        stream.Finish();
        return stream.GetSnapshot(configuration.SegmentationOptions);
    }

    static DiarizationSnapshot ProcessConfiguredOffline(string modelPath, float[] samples, int sampleRate)
    {
        DiarizationConfiguration configuration = CreateConfiguration();
        using var model = DiarizationModel.Load(modelPath, configuration.ModelOptions);
        return model.ProcessOffline(samples, sampleRate, configuration.SegmentationOptions);
    }
    // sdk-example-end

    // sdk-example: revision-polling
    static (DiarizationResultUpdate Update, bool IsIncomplete) PollRevisionAwareUpdate(DiarizationStream stream)
    {
        try
        {
            return (stream.ReadResultUpdates(), false);
        }
        catch (DiarizationFrameHistoryGapException)
        {
            DiarizationResultUpdate recovered = stream.ReadResultUpdates(allowHistoryGap: true);
            return (recovered, recovered.HistoryGap.HasValue);
        }
    }

    static void DisplayLatestResult(DiarizationStream stream)
    {
        (DiarizationResultUpdate update, bool incomplete) = PollRevisionAwareUpdate(stream);
        Console.WriteLine($"Update {update.Sequence}, frames [{update.FirstRetainedFrame}, {update.FrameCount}), final={update.IsFinal}");
        if (update.HistoryGap is { } gap)
            Console.WriteLine($"Missing frames [{gap.StartFrame}, {gap.EndFrameExclusive})");
        foreach (DiarizationFrameBatch revision in update.RevisedFrames)
            Console.WriteLine($"Revised [{revision.FirstFrameIndex}, {revision.FirstFrameIndex + revision.FrameCount})");
        Console.WriteLine(incomplete ? "Probability history is incomplete after a reported gap." : "No unread history gap.");
        IReadOnlyList<DiarizationSegment> latestSegments = update.Segments;
        Console.WriteLine($"Latest replacement segment count: {latestSegments.Count}");
    }
    // sdk-example-end

    // sdk-example: synthetic-postprocessing
    static string CreateSyntheticRttm()
    {
        var history = new DiarizationProbabilityHistory(maxRetainedFrames: null);
        history.Append(DiarizationFrameBatch.FromProbabilities(0, 1, 1.0, [0.8f, 0.1f]));
        history.Append(DiarizationFrameBatch.FromProbabilities(2, 1, 1.0, [0.8f]));

        var options = new DiarizationPostprocessingOptions
        {
            Onset = 0.5f,
            Offset = 0.5f,
            PadOffsetSeconds = 0.5,
            MinGapSeconds = 0.5,
            MinDurationSeconds = 0
        };
        IReadOnlyList<DiarizationSegment> segments = DiarizationPostprocessor.Segment(history.GetFrames(), options);
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        DiarizationPostprocessor.WriteRttm(writer, segments, recordingId: "synthetic");
        return writer.ToString();
    }
    // sdk-example-end

    // sdk-example: async-results
    static async Task<IReadOnlyList<DiarizationSegment>> ProcessAsyncResults(
        DiarizationStream stream,
        IAsyncEnumerable<ReadOnlyMemory<float>> monoPcmChunks,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        var history = new DiarizationProbabilityHistory(maxRetainedFrames: 12_000);
        await foreach (DiarizationResultUpdate update in
            stream.ProcessStreamAsync(monoPcmChunks, sampleRate, cancellationToken))
        {
            history.Apply(update);
            IReadOnlyList<DiarizationSegment> provisionalSegments = DiarizationPostprocessor.Segment(
                history.GetFrames(), new DiarizationPostprocessingOptions());
            if (update.IsFinal)
                return provisionalSegments;
        }

        return Array.Empty<DiarizationSegment>();
    }
    // sdk-example-end

    // sdk-example: consumer-api
    static DiarizationSnapshot ProcessConsumerPcm(
        string modelPath,
        string nativeRuntimeDirectory,
        ReadOnlySpan<float> monoPcm,
        int sampleRate,
        string? rttmPath = null)
    {
        Environment.SetEnvironmentVariable("NEMOTONDIARIZATIONSHARP_NATIVE_DIR",
            Path.GetFullPath(nativeRuntimeDirectory));
        using var model = DiarizationModel.Load(Path.GetFullPath(modelPath),
            new DiarizationModelOptions { Backend = DiarizationBackend.Cpu });
        DiarizationSnapshot result = model.ProcessOffline(monoPcm, sampleRate);

        if (result.FrameCount > 0)
            Console.WriteLine($"Frame 0 at {result.GetFrameTimestamp(0):F2}s: " +
                $"speaker channel 0 probability {result.GetProbability(0, 0):F3} of {result.SpeakerCount}");
        foreach (DiarizationSegment segment in result.Segments)
            Console.WriteLine($"{segment.StartTime:F2}-{segment.EndTime:F2}: speaker {segment.Speaker}");

        if (rttmPath is not null)
        {
            using var writer = File.CreateText(rttmPath);
            DiarizationPostprocessor.WriteRttm(writer, result.Segments, recordingId: "recording");
        }
        return result;
    }
    // sdk-example-end
}
