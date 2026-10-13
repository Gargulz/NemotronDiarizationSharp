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
        Assert.Null(options.GpuDeviceIndex);
        Assert.Null(options.ChunkFrames);
        Assert.Equal(0, options.LeftContextFrames);
        Assert.Equal(-1, options.GetNativeGpuDeviceIndex());
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
    [InlineData("README.md", "preset-options")]
    [InlineData("README.ru.md", "preset-options")]
    [InlineData("README.md", "json-configuration")]
    [InlineData("README.ru.md", "json-configuration")]
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

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NemotronDiarizationSharp.sln")))
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
}
