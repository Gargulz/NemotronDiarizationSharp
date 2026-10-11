using System.Text.Json;
using System.Text.Json.Serialization;

namespace NemotronDiarizationSharp.NativeTestHost;

[JsonConverter(typeof(JsonStringEnumConverter<NativeBackend>))]
public enum NativeBackend
{
    Unknown,
    Cpu,
    Cuda
}

[JsonConverter(typeof(JsonStringEnumConverter<NativeTestMode>))]
public enum NativeTestMode
{
    Unknown,
    Preflight,
    Inference
}

public sealed record NativeFileHash
{
    public string Name { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
}

public sealed record NativeTestRequest
{
    public int ProtocolVersion { get; init; }
    public string RequestId { get; init; } = string.Empty;
    public NativeTestMode Mode { get; init; }
    public NativeBackend Backend { get; init; }
    public int? GpuDeviceIndex { get; init; }
    public string NativeRuntimeIdentity { get; init; } = string.Empty;
    public string NativeStageDirectory { get; init; } = string.Empty;
    public IReadOnlyList<NativeFileHash> NativeFiles { get; init; } = [];
    public string ModelPath { get; init; } = string.Empty;
    public string AudioPath { get; init; } = string.Empty;
    public string InputFormat { get; init; } = string.Empty;
    public int SampleRate { get; init; }
    public double AudioStartSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public string RecordingId { get; init; } = string.Empty;
    public string PredictionOutputPath { get; init; } = string.Empty;
    public string ResultOutputPath { get; init; } = string.Empty;
}

public sealed record NativeTestResponse
{
    public int ProtocolVersion { get; init; } = 1;
    public string RequestId { get; init; } = string.Empty;
    public string Status { get; init; } = "failed";
    public string ErrorCode { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;
    public string SelectedBackend { get; init; } = string.Empty;
    public int? RequestedGpuIndex { get; init; }
    public string NativeRuntimeIdentity { get; init; } = string.Empty;
    public bool NativeRuntimeLoaded { get; init; }
    public double PreflightMilliseconds { get; init; }
    public double ModelLoadMilliseconds { get; init; }
    public double StreamOpenMilliseconds { get; init; }
    public double InferenceMilliseconds { get; init; }
    public double TotalMilliseconds { get; init; }
    public string ProcessPriority { get; init; } = string.Empty;
    public bool ProcessPriorityApplied { get; init; }
    public string InferenceMeasurementScope { get; init; } = string.Empty;
    public long FrameCount { get; init; }
    public long FirstProbabilityFrame { get; init; }
    public int SpeakerCount { get; init; }
    public double SecondsPerFrame { get; init; }
    public double ProbabilityMinimum { get; init; }
    public double ProbabilityMaximum { get; init; }
    public bool AllProbabilitiesFiniteAndBounded { get; init; }
    public bool IsFinal { get; init; }
    public bool NativeResourcesDisposed { get; init; }
    public IReadOnlyList<NativeSegmentResult> Segments { get; init; } = [];
    public IReadOnlyDictionary<string, string> LoadedNativeModules { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string PredictionRttmPath { get; init; } = string.Empty;
}

public sealed record NativeSegmentResult(double StartTime, double EndTime, int Speaker);

public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };
}
