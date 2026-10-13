using System.Text.Json;
using System.Text.Json.Serialization;

namespace NemotronDiarizationSharp;

/// <summary>Optional managed JSON configuration using the existing model and segmentation options.</summary>
public sealed class DiarizationConfiguration
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    private DiarizationConfiguration(DiarizationModelOptions modelOptions,
        DiarizationSegmentationOptions? segmentationOptions)
    {
        ModelOptions = modelOptions;
        SegmentationOptions = segmentationOptions;
    }

    /// <summary>Model options; an omitted or null JSON section produces default options.</summary>
    public DiarizationModelOptions ModelOptions { get; }

    /// <summary>Segmentation options; an omitted or null JSON section remains null.</summary>
    public DiarizationSegmentationOptions? SegmentationOptions { get; }

    /// <summary>Parses a strict JSON object with optional modelOptions and segmentationOptions sections.</summary>
    /// <remarks>
    /// Property names are case-sensitive camelCase. Omitted or null scalar values retain
    /// existing native sentinel semantics. An empty segmentationOptions object creates
    /// explicit default segmentation options. Validation is managed and allocates no native resources.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The JSON string is null.</exception>
    /// <exception cref="JsonException">The document, schema, or managed option values are invalid.</exception>
    public static DiarizationConfiguration Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonOptions parsed = JsonSerializer.Deserialize<JsonOptions>(json, SerializerOptions)
            ?? throw new JsonException("The configuration root must be a JSON object.");
        DiarizationModelOptions modelOptions = parsed.ModelOptions ?? new DiarizationModelOptions();

        try
        {
            modelOptions.GetNativeGpuDeviceIndex();
            modelOptions.ValidateGeometryArithmetic();
            parsed.SegmentationOptions?.ToNativeConfig();
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("Invalid diarization configuration: " + exception.Message, exception);
        }

        return new DiarizationConfiguration(modelOptions, parsed.SegmentationOptions);
    }

    private sealed class JsonOptions
    {
        public JsonOptions() { }

        public DiarizationModelOptions? ModelOptions { get; init; }
        public DiarizationSegmentationOptions? SegmentationOptions { get; init; }
    }
}
