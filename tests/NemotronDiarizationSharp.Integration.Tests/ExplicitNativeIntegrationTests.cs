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
        AssertCompleted(response, "cpu", expectedGpuIndex: -1, request);
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
        AssertCompleted(response, "cuda", RequiredGpuIndex(), request);
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
        NativeRequest cpuRequest = CreateRequest("cpu", NativeMode.Inference,
            cpuStage, cpuManifest, model, audioPath, "wavPcm16Le", startSeconds, 180, recordingId);
        HostResponse cpu = await ExecuteInferenceAsync(cpuRequest);
        AssertCompleted(cpu, "cpu", expectedGpuIndex: -1, cpuRequest);
        NativeRequest cudaRequest = CreateRequest("cuda", NativeMode.Inference,
            cudaStage, cudaManifest, model, audioPath, "wavPcm16Le", startSeconds, 180, recordingId);
        HostResponse cuda = await ExecuteInferenceAsync(cudaRequest);
        AssertCompleted(cuda, "cuda", RequiredGpuIndex(), cudaRequest);

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
        VerifyFixtureHash(modelPath, "08456d9e22cd9a323c0364d98375f3746d6e68507ebb705cd46438c534c7a3a1");
        if (string.Equals(inputFormat, "pcmF32Le", StringComparison.Ordinal))
            VerifyFixtureHash(audioPath, "23978983c7b52c95f8dabf7639ff64fe9629bd0d094cd208992eeddfbe9a72d0");
        string outputDirectory = RequiredPath("NEMOTONDIARIZATIONSHARP_A3_OUTPUT_DIRECTORY");
        Directory.CreateDirectory(outputDirectory);
        string requestId = $"{recordingId}-{backend}-{Guid.NewGuid():N}";
        return new NativeRequest
        {
            ProtocolVersion = 1,
            RequestId = requestId,
            Mode = mode,
            Backend = backend,
            GpuDeviceIndex = backend == "cuda" ? RequiredGpuIndex() : -1,
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
            NativeRequest serializedRequest = JsonSerializer.Deserialize<NativeRequest>(await File.ReadAllTextAsync(requestPath), JsonOptions)
                ?? throw new InvalidDataException("The child-process request JSON did not deserialize.");
            Assert.Equal(request.RequestId, serializedRequest.RequestId);
            Assert.Equal(request.Backend, serializedRequest.Backend);
            Assert.Equal(request.GpuDeviceIndex, serializedRequest.GpuDeviceIndex);
            Assert.Equal(request.NativeRuntimeIdentity, serializedRequest.NativeRuntimeIdentity);
            Assert.Equal(Path.GetFullPath(request.NativeStageDirectory), serializedRequest.NativeStageDirectory);
            Assert.Equal(Path.GetFullPath(request.ModelPath), serializedRequest.ModelPath);
            Assert.Equal(Path.GetFullPath(request.AudioPath), serializedRequest.AudioPath);
            Assert.Equal(Path.GetFullPath(request.PredictionOutputPath), serializedRequest.PredictionOutputPath);
            Assert.Equal(Path.GetFullPath(request.ResultOutputPath), serializedRequest.ResultOutputPath);
            Assert.Equal(request.DurationSeconds, serializedRequest.DurationSeconds);

            ProcessStartInfo startInfo = ChildProcessRunner.CreateStartInfo(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                [hostAssembly, "--request", requestPath], Path.GetDirectoryName(hostAssembly)!);
            ChildProcessResult process = await new ChildProcessRunner(new SystemChildProcessLauncher())
                .RunAsync(startInfo, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
            Assert.True(process.Started, $"Could not launch native test host: {process.ErrorMessage}");
            Assert.False(process.TimedOut, $"Native test host exceeded its five minute timeout. stderr: {process.StandardError}");
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

    private static void AssertCompleted(HostResponse response, string backend, int? expectedGpuIndex, NativeRequest request)
    {
        Assert.Equal(backend, response.SelectedBackend);
        Assert.Equal(expectedGpuIndex, response.RequestedGpuIndex);
        Assert.True(response.NativeRuntimeLoaded);
        Assert.StartsWith("nemo-speech-asr 0.2.0", response.NativeRuntimeIdentity, StringComparison.Ordinal);
        Assert.True(response.ModelLoadMilliseconds >= 0);
        Assert.True(response.StreamOpenMilliseconds >= 0);
        Assert.True(response.InferenceMilliseconds > 0);
        Assert.Contains("Stream Push and Finish only", response.InferenceMeasurementScope, StringComparison.Ordinal);
        Assert.True(response.NativeResourcesDisposed);
        Assert.True(response.IsFinal);
        Assert.Equal(0, response.FirstProbabilityFrame);
        Assert.Equal(8, response.SpeakerCount);
        Assert.InRange(response.SecondsPerFrame, 0.009, 0.011);
        if (request.DurationSeconds == 8)
            Assert.Equal(801, response.FrameCount);
        else
            Assert.Equal((long)Math.Round(request.DurationSeconds / response.SecondsPerFrame) + 1, response.FrameCount);
        Assert.True(response.AllProbabilitiesFiniteAndBounded);
        Assert.InRange(response.ProbabilityMinimum, 0d, 1d);
        Assert.InRange(response.ProbabilityMaximum, 0d, 1d);
        Assert.True(response.ProbabilityMinimum <= response.ProbabilityMaximum);
        Assert.NotEmpty(response.Segments);
        double previousStart = -1;
        foreach (HostSegment segment in response.Segments)
        {
            Assert.True(double.IsFinite(segment.StartTime));
            Assert.True(double.IsFinite(segment.EndTime));
            Assert.True(segment.StartTime >= previousStart);
            Assert.True(segment.StartTime >= 0 && segment.EndTime > segment.StartTime);
            Assert.True(segment.EndTime <= request.DurationSeconds + response.SecondsPerFrame);
            Assert.InRange(segment.Speaker, 1, response.SpeakerCount);
            previousStart = segment.StartTime;
        }
        if (request.DurationSeconds == 8)
        {
            HostSegment[] speakerOne = response.Segments.Where(segment => segment.Speaker == 1).ToArray();
            Assert.NotEmpty(speakerOne);
            Assert.InRange(Math.Abs(speakerOne.Min(static segment => segment.StartTime) - 0.85), 0, 0.5);
            Assert.InRange(Math.Abs(speakerOne.Max(static segment => segment.EndTime) - 3.81), 0, 0.5);
        }

        string[] requiredModules = backend == "cuda"
            ? ["nemo_speech_asr_c.dll", "nemo_speech_asr.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll",
                "ggml-cuda.dll", "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll", "msvcp140.dll",
                "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll", "nvcuda.dll"]
            : ["nemo_speech_asr_c.dll", "nemo_speech_asr.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll"];
        foreach (string moduleName in requiredModules)
        {
            string? modulePath = response.LoadedNativeModules
                .Where(entry => string.Equals(entry.Key, moduleName, StringComparison.OrdinalIgnoreCase))
                .Select(static entry => entry.Value)
                .FirstOrDefault();
            Assert.NotNull(modulePath);
            string expectedDirectory = string.Equals(moduleName, "nvcuda.dll", StringComparison.OrdinalIgnoreCase)
                ? Environment.SystemDirectory
                : Path.GetFullPath(request.NativeStageDirectory);
            string expectedPath = Path.GetFullPath(Path.Combine(expectedDirectory, moduleName));
            Assert.True(string.Equals(expectedPath, Path.GetFullPath(modulePath), StringComparison.OrdinalIgnoreCase),
                $"Native module {moduleName} loaded from {modulePath}, expected {expectedPath}.");
        }
        if (backend == "cpu")
            Assert.DoesNotContain(response.LoadedNativeModules.Keys, name => name.Contains("cuda", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("cublas", StringComparison.OrdinalIgnoreCase));

        using (var reader = File.OpenText(response.PredictionRttmPath))
        {
            IReadOnlyList<RttmSegment> rttm = RttmReader.Read(reader);
            Assert.NotEmpty(rttm);
            Assert.All(rttm, segment =>
            {
                Assert.True(double.IsFinite(segment.StartSeconds));
                Assert.True(double.IsFinite(segment.DurationSeconds));
                Assert.True(segment.StartSeconds >= 0 && segment.DurationSeconds > 0);
                Assert.True(segment.EndSeconds <= request.DurationSeconds + response.SecondsPerFrame);
            });
        }
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

    private static void VerifyFixtureHash(string path, string expectedHash)
    {
        using FileStream stream = File.OpenRead(path);
        string actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        Assert.Equal(expectedHash, actualHash, ignoreCase: true);
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
