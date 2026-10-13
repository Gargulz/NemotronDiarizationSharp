using System.Globalization;
using System.Text.Json;
using NemotronDiarizationSharp.Native;
using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class JsonConfigurationTests
{
    private static readonly string[] ModelProperties =
    [
        "gpuDeviceIndex", "preset", "chunkFrames", "rightContextFrames", "leftContextFrames",
        "fifoFrames", "speakerCacheFrames", "updatePeriodFrames"
    ];

    private static readonly string[] SegmentationProperties =
    [
        "onset", "offset", "padOnsetSeconds", "padOffsetSeconds", "minGapSeconds", "minDurationSeconds"
    ];

    [Fact]
    public void ParsesEveryExistingOptionAndPreservesNativeMapping()
    {
        DiarizationConfiguration configuration = DiarizationConfiguration.Parse("""
            {
              "modelOptions": {
                "gpuDeviceIndex": 2, "preset": "v3-streaming", "chunkFrames": 17,
                "rightContextFrames": 3, "leftContextFrames": 0, "fifoFrames": 40,
                "speakerCacheFrames": 300, "updatePeriodFrames": 20
              },
              "segmentationOptions": {
                "onset": 0.75, "offset": 0.5, "padOnsetSeconds": 0.125,
                "padOffsetSeconds": 0.25, "minGapSeconds": 0.375, "minDurationSeconds": 0.5
              }
            }
            """);

        Assert.Equal(2, configuration.ModelOptions.GpuDeviceIndex);
        Assert.Equal("v3-streaming", configuration.ModelOptions.Preset);
        Assert.Equal(2, configuration.ModelOptions.GetNativeGpuDeviceIndex());
        var model = new DiarModelConfig();
        configuration.ModelOptions.ApplyNativeOverrides(ref model);
        Assert.Equal(17, model.ChunkFrames);
        Assert.Equal(3, model.RightContextFrames);
        Assert.Equal(0, model.LeftContextFrames);
        Assert.Equal(40, model.FifoFrames);
        Assert.Equal(300, model.SpeakerCacheFrames);
        Assert.Equal(20, model.UpdatePeriodFrames);
        Assert.Equal(17, configuration.ModelOptions.ChunkFrames);
        Assert.Equal(3, configuration.ModelOptions.RightContextFrames);
        Assert.Equal(0, configuration.ModelOptions.LeftContextFrames);
        Assert.Equal(40, configuration.ModelOptions.FifoFrames);
        Assert.Equal(300, configuration.ModelOptions.SpeakerCacheFrames);
        Assert.Equal(20, configuration.ModelOptions.UpdatePeriodFrames);

        DiarizationSegmentationOptions segmentation = Assert.IsType<DiarizationSegmentationOptions>(
            configuration.SegmentationOptions);
        NativeSegmentationConfig native = segmentation.ToNativeConfig();
        Assert.Equal(0.75f, segmentation.Onset);
        Assert.Equal(0.5f, segmentation.Offset);
        Assert.Equal(0.125, segmentation.PadOnsetSeconds);
        Assert.Equal(0.25, segmentation.PadOffsetSeconds);
        Assert.Equal(0.375, segmentation.MinGapSeconds);
        Assert.Equal(0.5, segmentation.MinDurationSeconds);
        Assert.Equal(0.75f, native.Onset);
        Assert.Equal(0.5f, native.Offset);
        Assert.Equal(0.125, native.PadOnsetSec);
        Assert.Equal(0.25, native.PadOffsetSec);
        Assert.Equal(0.375, native.MinGapSec);
        Assert.Equal(0.5, native.MinDurationSec);
    }

    [Fact]
    public void ParsesNemotron3ProfileAndAppliesJsonGeometryOverrides()
    {
        DiarizationConfiguration configuration = DiarizationConfiguration.Parse("""
            {"modelOptions":{"preset":"nemotron3-low-latency","chunkFrames":12,"updatePeriodFrames":100}}
            """);

        Assert.Equal(DiarizationPresets.Nemotron3LowLatency, configuration.ModelOptions.Preset);
        Assert.Equal(DiarizationPresets.V3Streaming, configuration.ModelOptions.GetNativePreset());
        var native = new DiarModelConfig();
        configuration.ModelOptions.ApplyNativeOverrides(ref native);
        Assert.Equal(12, native.ChunkFrames);
        Assert.Equal(4, native.RightContextFrames);
        Assert.Equal(0, native.LeftContextFrames);
        Assert.Equal(264, native.FifoFrames);
        Assert.Equal(264, native.SpeakerCacheFrames);
        Assert.Equal(100, native.UpdatePeriodFrames);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"modelOptions\":null}")]
    [InlineData("{\"modelOptions\":{}}")]
    [InlineData("{\"segmentationOptions\":null}")]
    public void OmittedOrNullSectionsRetainDefaultModelAndAbsentSegmentation(string json)
    {
        DiarizationConfiguration configuration = DiarizationConfiguration.Parse(json);
        Assert.NotNull(configuration.ModelOptions);
        Assert.Null(configuration.SegmentationOptions);
        foreach (string property in ModelProperties)
            Assert.Null(GetScalar(configuration, "modelOptions", property));

        Assert.Equal(-1, configuration.ModelOptions.GetNativeGpuDeviceIndex());
        var native = new DiarModelConfig();
        configuration.ModelOptions.ApplyNativeOverrides(ref native);
        Assert.Equal(0, native.ChunkFrames);
        Assert.Equal(0, native.RightContextFrames);
        Assert.Equal(-1, native.LeftContextFrames);
        Assert.Equal(0, native.FifoFrames);
        Assert.Equal(0, native.SpeakerCacheFrames);
        Assert.Equal(0, native.UpdatePeriodFrames);
    }

    [Fact]
    public void EmptySegmentationSectionIsExplicitDefaultOptions()
    {
        DiarizationConfiguration configuration = DiarizationConfiguration.Parse("{\"segmentationOptions\":{}}");
        DiarizationSegmentationOptions segmentation = Assert.IsType<DiarizationSegmentationOptions>(
            configuration.SegmentationOptions);
        foreach (string property in SegmentationProperties)
            Assert.Null(GetScalar(configuration, "segmentationOptions", property));

        NativeSegmentationConfig native = segmentation.ToNativeConfig();
        Assert.Equal(0f, native.Onset);
        Assert.Equal(0f, native.Offset);
        Assert.Equal(0d, native.PadOnsetSec);
        Assert.Equal(0d, native.PadOffsetSec);
        Assert.Equal(0d, native.MinGapSec);
        Assert.Equal(0d, native.MinDurationSec);
    }

    public static IEnumerable<object[]> ScalarProperties()
    {
        foreach (string property in ModelProperties)
            yield return ["modelOptions", property];
        foreach (string property in SegmentationProperties)
            yield return ["segmentationOptions", property];
    }

    [Theory]
    [MemberData(nameof(ScalarProperties))]
    public void EveryScalarCanBeExplicitNullOrOmitted(string section, string property)
    {
        DiarizationConfiguration explicitNull = DiarizationConfiguration.Parse(
            ScalarJson(section, property, "null"));
        DiarizationConfiguration omitted = DiarizationConfiguration.Parse("{\"" + section + "\":{}}");
        Assert.Null(GetScalar(explicitNull, section, property));
        Assert.Null(GetScalar(omitted, section, property));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("\"configuration\"")]
    [InlineData("{\"modelOptions\":[]}")]
    [InlineData("{\"modelOptions\":true}")]
    [InlineData("{\"modelOptions\":1}")]
    [InlineData("{\"modelOptions\":\"settings\"}")]
    [InlineData("{\"segmentationOptions\":[]}")]
    [InlineData("{\"segmentationOptions\":true}")]
    [InlineData("{\"segmentationOptions\":1}")]
    [InlineData("{\"segmentationOptions\":\"settings\"}")]
    public void RejectsNonObjectRootAndNonObjectSections(string json)
        => Assert.Throws<JsonException>(() => DiarizationConfiguration.Parse(json));

    [Theory]
    [InlineData("{\"unknown\":null}")]
    [InlineData("{\"modelOptions\":{\"unknown\":null}}")]
    [InlineData("{\"segmentationOptions\":{\"unknown\":null}}")]
    [InlineData("{\"ModelOptions\":{}}")]
    [InlineData("{\"SegmentationOptions\":{}}")]
    [InlineData("{\"modelOptions\":{\"ChunkFrames\":1}}")]
    [InlineData("{\"segmentationOptions\":{\"Onset\":0.5}}")]
    public void RejectsUnknownPropertiesAndWrongCaseAtEveryObjectLevel(string json)
        => Assert.Throws<JsonException>(() => DiarizationConfiguration.Parse(json));

    public static IEnumerable<object[]> DuplicateProperties()
    {
        foreach (string section in new[] { "modelOptions", "segmentationOptions" })
        {
            yield return ["{\"" + section + "\":null,\"" + section + "\":null}"];
            yield return ["{\"" + section + "\":null,\"" + EscapedName(section) + "\":null}"];
        }
        foreach (object[] field in ScalarProperties())
        {
            string section = (string)field[0];
            string property = (string)field[1];
            yield return ["{\"" + section + "\":{\"" + property + "\":null,\"" + property + "\":null}}"];
            yield return ["{\"" + section + "\":{\"" + property + "\":null,\"" + EscapedName(property) + "\":null}}"];
        }
    }

    [Theory]
    [MemberData(nameof(DuplicateProperties))]
    public void RejectsDuplicatesIncludingEscapedEquivalentNames(string json)
        => Assert.Throws<JsonException>(() => DiarizationConfiguration.Parse(json));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{")]
    [InlineData("{modelOptions:{}}")]
    [InlineData("{\"modelOptions\":}")]
    [InlineData("{/*comment*/}")]
    [InlineData("{\"modelOptions\":{/*comment*/}}")]
    [InlineData("{\"segmentationOptions\":{/*comment*/}}")]
    [InlineData("//comment\n{}")]
    [InlineData("{\"modelOptions\":{},}")]
    [InlineData("{\"modelOptions\":{\"chunkFrames\":1,}}")]
    [InlineData("{\"segmentationOptions\":{\"onset\":0.5,}}")]
    [InlineData("{} {}")]
    [InlineData("{} true")]
    [InlineData("{\"modelOptions\":{\"chunkFrames\":NaN}}")]
    public void RejectsMalformedDocumentsCommentsTrailingCommasAndTrailingContent(string json)
        => Assert.Throws<JsonException>(() => DiarizationConfiguration.Parse(json));

    [Fact]
    public void NullInputThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => DiarizationConfiguration.Parse(null!));

    public static IEnumerable<object[]> WrongScalarKinds()
    {
        foreach (object[] field in ScalarProperties())
        {
            string section = (string)field[0];
            string property = (string)field[1];
            string[] invalidValues = property == "preset" ? ["1", "true", "[]", "{}"] : ["\"1\"", "true", "[]", "{}"];
            foreach (string invalidValue in invalidValues)
                yield return [section, property, invalidValue];
        }
    }

    [Theory]
    [MemberData(nameof(WrongScalarKinds))]
    public void RejectsWrongJsonKindsForEveryScalar(string section, string property, string value)
        => Assert.Throws<JsonException>(() => DiarizationConfiguration.Parse(ScalarJson(section, property, value)));

    public static IEnumerable<object[]> InvalidIntegerValues()
    {
        foreach (string property in ModelProperties.Where(property => property != "preset"))
            foreach (string value in new[] { "1.5", "1.0", "1e0", "2147483648", "-2147483649", "1e1000" })
                yield return [property, value];
    }

    [Theory]
    [MemberData(nameof(InvalidIntegerValues))]
    public void RejectsFractionalOrOutOfRangeIntegers(string property, string value)
        => Assert.Throws<JsonException>(() => DiarizationConfiguration.Parse(ScalarJson("modelOptions", property, value)));

    public static IEnumerable<object[]> InvalidFloatingValues()
    {
        foreach (string property in SegmentationProperties)
        {
            bool single = property is "onset" or "offset";
            foreach (string value in new[] { single ? "1e39" : "1e309", single ? "-1e39" : "-1e309", "\"NaN\"", "\"Infinity\"", "\"-Infinity\"" })
                yield return [property, value];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidFloatingValues))]
    public void RejectsNonFiniteFloatingConversionsAndNamedStrings(string property, string value)
        => Assert.Throws<JsonException>(() => DiarizationConfiguration.Parse(ScalarJson("segmentationOptions", property, value)));

    [Theory]
    [InlineData("gpuDeviceIndex", "-1")]
    [InlineData("chunkFrames", "2147483647")]
    [InlineData("rightContextFrames", "2147483647")]
    [InlineData("leftContextFrames", "2147483647")]
    [InlineData("fifoFrames", "2147483647")]
    [InlineData("speakerCacheFrames", "2147483647")]
    public void WrapsManagedSemanticValidationAsJsonException(string property, string value)
    {
        JsonException exception = Assert.Throws<JsonException>(() =>
            DiarizationConfiguration.Parse(ScalarJson("modelOptions", property, value)));
        Assert.IsType<ArgumentOutOfRangeException>(exception.InnerException);
        Assert.Contains("Invalid diarization configuration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesGeometrySentinelsAndMaximumUpdatePeriod()
    {
        DiarizationConfiguration configuration = DiarizationConfiguration.Parse("""
            {"modelOptions":{"preset":"v3-offline","chunkFrames":-2147483648,
            "rightContextFrames":0,"leftContextFrames":0,"fifoFrames":-1,
            "speakerCacheFrames":-2147483648,"updatePeriodFrames":2147483647}}
            """);
        var native = new DiarModelConfig();
        configuration.ModelOptions.ApplyNativeOverrides(ref native);
        Assert.Equal(int.MinValue, native.ChunkFrames);
        Assert.Equal(0, native.RightContextFrames);
        Assert.Equal(0, native.LeftContextFrames);
        Assert.Equal(-1, native.FifoFrames);
        Assert.Equal(int.MinValue, native.SpeakerCacheFrames);
        Assert.Equal(int.MaxValue, native.UpdatePeriodFrames);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("\0v3-offline")]
    [InlineData("v3-streaming\0suffix")]
    public void PreservesNativePresetStrings(string preset)
    {
        string json = ScalarJson("modelOptions", "preset", JsonSerializer.Serialize(preset));
        Assert.Equal(preset, DiarizationConfiguration.Parse(json).ModelOptions.Preset);
    }

    [Fact]
    public void NulPrefixedKnownPresetStillReceivesGeometryValidation()
        => Assert.Throws<JsonException>(() => DiarizationConfiguration.Parse(
            "{\"modelOptions\":{\"preset\":\"v3-streaming\\u0000suffix\",\"chunkFrames\":2147483647}}"));

    public static IEnumerable<object[]> FiniteSegmentationExtremes()
    {
        foreach (string property in SegmentationProperties)
        {
            bool single = property is "onset" or "offset";
            string maximum = single ? float.MaxValue.ToString("R", CultureInfo.InvariantCulture)
                : double.MaxValue.ToString("R", CultureInfo.InvariantCulture);
            yield return [property, "0"];
            yield return [property, "-1"];
            yield return [property, maximum];
            yield return [property, "-" + maximum];
        }
    }

    [Theory]
    [MemberData(nameof(FiniteSegmentationExtremes))]
    public void PreservesFiniteSegmentationValuesWithoutImposingNativeLimits(string property, string value)
    {
        DiarizationConfiguration configuration = DiarizationConfiguration.Parse(
            ScalarJson("segmentationOptions", property, value));
        object? actual = GetScalar(configuration, "segmentationOptions", property);
        if (property is "onset" or "offset")
            Assert.Equal(float.Parse(value, CultureInfo.InvariantCulture), Assert.IsType<float>(actual));
        else
            Assert.Equal(double.Parse(value, CultureInfo.InvariantCulture), Assert.IsType<double>(actual));
    }

    [Fact]
    public void LeavesModelSpecificGeometryConstraintsToNative()
    {
        DiarizationConfiguration configuration = DiarizationConfiguration.Parse(
            "{\"modelOptions\":{\"speakerCacheFrames\":1,\"chunkFrames\":1000000}}");
        Assert.Equal(1, configuration.ModelOptions.SpeakerCacheFrames);
        Assert.Equal(1_000_000, configuration.ModelOptions.ChunkFrames);
    }

    [Fact]
    public void ExistingLoadOverloadsRemainUnambiguousWithoutLoadingNativeResources()
    {
        Func<string, DiarizationModel> defaultLoad = DiarizationModel.Load;
        Func<string, DiarizationModelOptions, DiarizationModel> configuredLoad = DiarizationModel.Load;
        Assert.NotNull(defaultLoad);
        Assert.NotNull(configuredLoad);
        DiarizationConfiguration configuration = DiarizationConfiguration.Parse("{}");
        Assert.Throws<FileNotFoundException>(() => configuredLoad(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".gguf"), configuration.ModelOptions));
    }

    private static string ScalarJson(string section, string property, string value)
        => "{\"" + section + "\":{\"" + property + "\":" + value + "}}";

    private static string EscapedName(string name)
        => "\\u" + ((int)name[0]).ToString("x4", CultureInfo.InvariantCulture) + name[1..];

    private static object? GetScalar(DiarizationConfiguration configuration, string section, string property)
    {
        object options = section == "modelOptions" ? configuration.ModelOptions : configuration.SegmentationOptions!;
        string managedProperty = char.ToUpperInvariant(property[0]) + property[1..];
        return options.GetType().GetProperty(managedProperty)!.GetValue(options);
    }
}
