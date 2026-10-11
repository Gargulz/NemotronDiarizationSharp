using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using NemotronDiarizationSharp.Evaluation;
using Xunit;

namespace NemotronDiarizationSharp.Integration.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeInferenceCollection
{
    public const string Name = "Native inference processes";
}

[Collection(NativeInferenceCollection.Name)]
public sealed class ExplicitNativeIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact(Explicit = true)]
    public async Task EightSecondCpuSmoke()
    {
        NativeRequest request = CreateRequest("cpu", NativeMode.Inference,
            RequiredPath("NEMOTONDIARIZATIONSHARP_A3_CPU_STAGE"),
            RequiredPath("NEMOTONDIARIZATIONSHARP_A3_CPU_MANIFEST"),
            RequiredPath("NEMOTONDIARIZATIONSHARP_MODEL"),
            RequiredPath("NEMOTONDIARIZATIONSHARP_A3_SMOKE_PCM_F32LE"), "pcmF32Le", 0, 8, "smoke");
        HostResponse response = await ExecuteInferenceAsync(request);
        AssertCompleted(response, "cpu", expectedGpuIndex: null);
        Assert.True(response.FrameCount > 0);
    }

    [Fact(Explicit = true)]
    public async Task EightSecondCudaSmoke()
    {
        NativeRequest request = CreateRequest("cuda", NativeMode.Inference,
            RequiredPath("NEMOTONDIARIZATIONSHARP_A3_CUDA_STAGE"),
            RequiredPath("NEMOTONDIARIZATIONSHARP_A3_CUDA_MANIFEST"),
            RequiredPath("NEMOTONDIARIZATIONSHARP_MODEL"),
            RequiredPath("NEMOTONDIARIZATIONSHARP_A3_SMOKE_PCM_F32LE"), "pcmF32Le", 0, 8, "smoke");
        HostResponse response = await ExecuteInferenceAsync(request);
        AssertCompleted(response, "cuda", RequiredGpuIndex());
        Assert.True(response.FrameCount > 0);
    }

    [Fact(Explicit = true)]
    public async Task Ts3003aCpuCudaComparisonScoresBothOutputs()
    {
        await CompareAmiExcerptAsync(
            "TS3003a", 300, RequiredPath("NEMOTONDIARIZATIONSHARP_A3_TS3003A_AUDIO_WAV"),
            RequiredPath("NEMOTONDIARIZATIONSHARP_A3_TS3003A_REFERENCE_RTTM"));
    }

    [Fact(Explicit = true)]
    public async Task En2002aCpuCudaComparisonScoresBothOutputs()
    {
        await CompareAmiExcerptAsync(
            "EN2002a", 900, RequiredPath("NEMOTONDIARIZATIONSHARP_A3_EN2002A_AUDIO_WAV"),
            RequiredPath("NEMOTONDIARIZATIONSHARP_A3_EN2002A_REFERENCE_RTTM"));
    }

    private static async Task CompareAmiExcerptAsync(string recordingId, double startSeconds, string audioPath, string referencePath)
    {
        string model = RequiredPath("NEMOTONDIARIZATIONSHARP_MODEL");
        string cpuStage = RequiredPath("NEMOTONDIARIZATIONSHARP_A3_CPU_STAGE");
        string cudaStage = RequiredPath("NEMOTONDIARIZATIONSHARP_A3_CUDA_STAGE");
        string cpuManifest = RequiredPath("NEMOTONDIARIZATIONSHARP_A3_CPU_MANIFEST");
        string cudaManifest = RequiredPath("NEMOTONDIARIZATIONSHARP_A3_CUDA_MANIFEST");

        // Await sequentially so CUDA never overlaps the CPU child or another GPU case.
        HostResponse cpu = await ExecuteInferenceAsync(CreateRequest("cpu", NativeMode.Inference,
            cpuStage, cpuManifest, model, audioPath, "wavPcm16Le", startSeconds, 180, recordingId));
        AssertCompleted(cpu, "cpu", expectedGpuIndex: null);
        HostResponse cuda = await ExecuteInferenceAsync(CreateRequest("cuda", NativeMode.Inference,
            cudaStage, cudaManifest, model, audioPath, "wavPcm16Le", startSeconds, 180, recordingId));
        AssertCompleted(cuda, "cuda", RequiredGpuIndex());

        IReadOnlyList<RttmSegment> absoluteReference;
        using (var reader = File.OpenText(referencePath))
            absoluteReference = RttmReader.Read(reader);
        var uem = new RttmTimeWindow(startSeconds, 180);
        RttmSegment[] clippedReference = absoluteReference
            .Select(uem.Clip)
            .Where(static segment => segment is not null)
            .Select(static segment => segment!)
            .ToArray();
        IReadOnlyList<RttmSegment> cpuPrediction = ReadRttm(cpu.PredictionRttmPath);
        IReadOnlyList<RttmSegment> cudaPrediction = ReadRttm(cuda.PredictionRttmPath);
        DiarizationErrorRateScore cpuScore = DiarizationErrorRateScorer.Score(clippedReference, cpuPrediction);
        DiarizationErrorRateScore cudaScore = DiarizationErrorRateScorer.Score(clippedReference, cudaPrediction);

        string reportDirectory = Path.Combine(Path.GetTempPath(), "NemotronDiarizationSharp", "A3.1", "reports");
        Directory.CreateDirectory(reportDirectory);
        string reportPath = Path.Combine(reportDirectory, $"{recordingId}-{Guid.NewGuid():N}.json");
        var report = new { recordingId, excerptStartSeconds = startSeconds, excerptDurationSeconds = 180, cpu, cpuScore, cuda, cudaScore };
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions));
        Assert.True(cpuScore.TotalReferenceSpeakerSeconds > 0, "The reference RTTM must overlap the selected UEM.");
        Assert.True(cudaScore.TotalReferenceSpeakerSeconds > 0, "The reference RTTM must overlap the selected UEM.");
    }

    private static NativeRequest CreateRequest(
        string backend,
        NativeMode mode,
        string stage,
        string manifestPath,
        string modelPath,
        string audioPath,
        string inputFormat,
        double startSeconds,
        double durationSeconds,
        string recordingId)
    {
        RuntimeManifest manifest = JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException($"Native runtime manifest is empty: {manifestPath}");
        Assert.Equal(backend, manifest.Backend);
        Assert.Equal("nemo-speech-asr 0.2.0", manifest.NativeRuntimeIdentity);
        string outputDirectory = Path.Combine(Path.GetTempPath(), "NemotronDiarizationSharp", "A3.1", "runs");
        Directory.CreateDirectory(outputDirectory);
        string requestId = $"{recordingId}-{backend}-{Guid.NewGuid():N}";
        return new NativeRequest
        {
            ProtocolVersion = 1,
            RequestId = requestId,
            Mode = mode,
            Backend = backend,
            GpuDeviceIndex = backend == "cuda" ? RequiredGpuIndex() : null,
            NativeRuntimeIdentity = manifest.NativeRuntimeIdentity,
            NativeStageDirectory = Path.GetFullPath(stage),
            NativeFiles = manifest.Files,
            ModelPath = Path.GetFullPath(modelPath),
            AudioPath = Path.GetFullPath(audioPath),
            InputFormat = inputFormat,
            SampleRate = 16000,
            AudioStartSeconds = startSeconds,
            DurationSeconds = durationSeconds,
            RecordingId = recordingId,
            PredictionOutputPath = Path.Combine(outputDirectory, requestId + ".pred.rttm"),
            ResultOutputPath = Path.Combine(outputDirectory, requestId + ".json")
        };
    }

    private static async Task<HostResponse> ExecuteInferenceAsync(NativeRequest request)
    {
        string hostAssembly = HostAssemblyLocator.Find();
        string requestPath = Path.Combine(Path.GetTempPath(), $"native-inference-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonOptions));
        try
        {
            ProcessStartInfo startInfo = ChildProcessRunner.CreateStartInfo(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                [hostAssembly, "--request", requestPath], Path.GetDirectoryName(hostAssembly)!);
            ChildProcessResult process = await new ChildProcessRunner(new SystemChildProcessLauncher())
                .RunAsync(startInfo, TimeSpan.FromMinutes(20), TestContext.Current.CancellationToken);
            Assert.True(process.Started, $"Could not launch native test host: {process.ErrorMessage}");
            Assert.False(process.TimedOut, $"Native test host exceeded its 20 minute timeout. stderr: {process.StandardError}");
            Assert.False(process.Cancelled, "Native test host was cancelled.");
            HostResponse response = JsonSerializer.Deserialize<HostResponse>(process.StandardOutput, TestJson.Options)
                ?? throw new InvalidDataException($"Native test host returned no JSON result. stderr: {process.StandardError}");
            Assert.Equal(0, process.ExitCode);
            Assert.True(response.Status == "completed",
                $"Native host failed before or during inference ({response.ErrorCode}): {response.ErrorMessage}; stderr: {process.StandardError}");
            return response;
        }
        finally
        {
            File.Delete(requestPath);
        }
    }

    private static void AssertCompleted(HostResponse response, string backend, int? expectedGpuIndex)
    {
        Assert.Equal(backend, response.SelectedBackend);
        Assert.Equal(expectedGpuIndex, response.RequestedGpuIndex);
        Assert.True(response.NativeRuntimeLoaded);
        Assert.StartsWith("nemo-speech-asr 0.2.0", response.NativeRuntimeIdentity, StringComparison.Ordinal);
        Assert.True(response.ModelLoadMilliseconds >= 0);
        Assert.True(response.StreamOpenMilliseconds >= 0);
        Assert.True(response.InferenceMilliseconds > 0);
        Assert.Contains("Stream Push and Finish only", response.InferenceMeasurementScope, StringComparison.Ordinal);
        Assert.True(File.Exists(response.PredictionRttmPath));
    }

    private static IReadOnlyList<RttmSegment> ReadRttm(string path)
    {
        using var reader = File.OpenText(path);
        return RttmReader.Read(reader);
    }

    private static string RequiredPath(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"Required explicit integration-test setting '{name}' must be an absolute path. The test does not skip missing prerequisites.");
        return value;
    }

    private static int RequiredGpuIndex()
    {
        string? value = Environment.GetEnvironmentVariable("NEMOTONDIARIZATIONSHARP_A3_GPU_DEVICE_INDEX");
        return string.IsNullOrWhiteSpace(value) ? 0 : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private enum NativeMode { Preflight, Inference }

    private sealed record RuntimeManifest
    {
        public string Backend { get; init; } = string.Empty;
        public string NativeRuntimeIdentity { get; init; } = string.Empty;
        public IReadOnlyList<NativeFileHash> Files { get; init; } = [];
    }

    private sealed record NativeRequest
    {
        public int ProtocolVersion { get; init; }
        public string RequestId { get; init; } = string.Empty;
        public NativeMode Mode { get; init; }
        public string Backend { get; init; } = string.Empty;
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
}
