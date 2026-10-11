using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NemotronDiarizationSharp.Evaluation;
using Xunit;

namespace NemotronDiarizationSharp.Integration.Tests;

public sealed class HostProtocolAndPreflightTests
{
    [Fact]
    public async Task ValidPreflightRequestRoundTripsAsStructuredJsonWithoutLoadingNativeCode()
    {
        using var fixture = SyntheticHostFixture.Create(NativeStageKind.Cpu);
        ChildProcessResult process = await RunHostAsync(fixture.Request, TestContext.Current.CancellationToken);

        Assert.True(process.Started);
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(process.StandardError);
        HostResponse response = Assert.IsType<HostResponse>(JsonSerializer.Deserialize<HostResponse>(process.StandardOutput, TestJson.Options));
        Assert.Equal("ready", response.Status);
        Assert.Equal("cpu", response.SelectedBackend);
        Assert.Equal("nemo-speech-asr 0.2.0", response.NativeRuntimeIdentity);
        Assert.False(response.NativeRuntimeLoaded);
        Assert.True(response.PreflightMilliseconds >= 0);
        Assert.True(File.Exists(fixture.Request.ResultOutputPath));
        Assert.Equal(response.Status, JsonSerializer.Deserialize<HostResponse>(
            File.ReadAllText(fixture.Request.ResultOutputPath), TestJson.Options)!.Status);
    }

    [Fact]
    public async Task InvalidJsonBackendIsReportedAsMachineReadableFailure()
    {
        using var fixture = SyntheticHostFixture.Create(NativeStageKind.Cpu);
        string json = JsonSerializer.Serialize(fixture.Request, TestJson.Options)
            .Replace("\"backend\":\"cpu\"", "\"backend\":\"metal\"", StringComparison.Ordinal);
        ChildProcessResult process = await RunRawHostRequestAsync(json, TestContext.Current.CancellationToken);

        Assert.Equal(2, process.ExitCode);
        HostResponse response = JsonSerializer.Deserialize<HostResponse>(process.StandardOutput, TestJson.Options)!;
        Assert.Equal("failed", response.Status);
        Assert.Equal("invalid_request_json", response.ErrorCode);
        Assert.NotEmpty(response.ErrorMessage);
    }

    [Fact]
    public async Task MissingModelFixtureFailsClearlyBeforeNativeLoading()
    {
        using var fixture = SyntheticHostFixture.Create(NativeStageKind.Cpu);
        fixture.Request = fixture.Request with { ModelPath = Path.Combine(fixture.Root, "missing-model.gguf") };
        ChildProcessResult process = await RunHostAsync(fixture.Request, TestContext.Current.CancellationToken);
        HostResponse response = JsonSerializer.Deserialize<HostResponse>(process.StandardOutput, TestJson.Options)!;

        Assert.Equal(2, process.ExitCode);
        Assert.Equal("missing_fixture", response.ErrorCode);
        Assert.Contains("Model file is missing", response.ErrorMessage, StringComparison.Ordinal);
        Assert.False(response.NativeRuntimeLoaded);
    }

    [Fact]
    public async Task MissingNativeStageFailsClearly()
    {
        using var fixture = SyntheticHostFixture.Create(NativeStageKind.Cpu);
        fixture.Request = fixture.Request with { NativeStageDirectory = Path.Combine(fixture.Root, "missing-stage") };
        ChildProcessResult process = await RunHostAsync(fixture.Request, TestContext.Current.CancellationToken);
        HostResponse response = JsonSerializer.Deserialize<HostResponse>(process.StandardOutput, TestJson.Options)!;

        Assert.Equal(2, process.ExitCode);
        Assert.Equal("invalid_native_stage", response.ErrorCode);
        Assert.Contains("stage directory", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CpuStageRecursivelyRejectsCudaRuntimeDlls()
    {
        using var fixture = SyntheticHostFixture.Create(NativeStageKind.Cpu);
        string nested = Path.Combine(fixture.Request.NativeStageDirectory, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllBytesAsync(Path.Combine(nested, "ggml-cuda.dll"), [1, 2, 3], TestContext.Current.CancellationToken);
        ChildProcessResult process = await RunHostAsync(fixture.Request, TestContext.Current.CancellationToken);
        HostResponse response = JsonSerializer.Deserialize<HostResponse>(process.StandardOutput, TestJson.Options)!;

        Assert.Equal(2, process.ExitCode);
        Assert.Equal("invalid_native_stage", response.ErrorCode);
        Assert.Contains("nested runtime layouts", response.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CpuStageRejectsCudaRuntimeDllAtRootAsMixedBackend()
    {
        using var fixture = SyntheticHostFixture.Create(NativeStageKind.Cpu, includeCudaFiles: true);
        ChildProcessResult process = await RunHostAsync(fixture.Request, TestContext.Current.CancellationToken);
        HostResponse response = JsonSerializer.Deserialize<HostResponse>(process.StandardOutput, TestJson.Options)!;

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("mixed CPU/CUDA", response.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeHashMismatchIsRejected()
    {
        using var fixture = SyntheticHostFixture.Create(NativeStageKind.Cpu);
        NativeFileHash first = fixture.Request.NativeFiles[0];
        fixture.Request = fixture.Request with
        {
            NativeFiles = [first with { Sha256 = new string('0', 64) }, .. fixture.Request.NativeFiles.Skip(1)]
        };
        ChildProcessResult process = await RunHostAsync(fixture.Request, TestContext.Current.CancellationToken);
        HostResponse response = JsonSerializer.Deserialize<HostResponse>(process.StandardOutput, TestJson.Options)!;

        Assert.Equal(2, process.ExitCode);
        Assert.Equal("native_hash_mismatch", response.ErrorCode);
        Assert.Contains("SHA-256 mismatch", response.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputsCannotOverwriteModelOrStagedRuntimeFiles()
    {
        using var fixture = SyntheticHostFixture.Create(NativeStageKind.Cpu);
        byte[] originalModel = await File.ReadAllBytesAsync(fixture.Request.ModelPath, TestContext.Current.CancellationToken);
        fixture.Request = fixture.Request with { ResultOutputPath = fixture.Request.ModelPath };
        ChildProcessResult modelCollision = await RunHostAsync(fixture.Request, TestContext.Current.CancellationToken);
        HostResponse modelResponse = JsonSerializer.Deserialize<HostResponse>(modelCollision.StandardOutput, TestJson.Options)!;
        Assert.Equal("invalid_request", modelResponse.ErrorCode);
        Assert.Equal(originalModel, await File.ReadAllBytesAsync(fixture.Request.ModelPath, TestContext.Current.CancellationToken));

        string stagedDll = Path.Combine(fixture.Request.NativeStageDirectory, fixture.Request.NativeFiles[0].Name);
        byte[] originalDll = await File.ReadAllBytesAsync(stagedDll, TestContext.Current.CancellationToken);
        fixture.Request = fixture.Request with
        {
            ResultOutputPath = Path.Combine(fixture.Root, "out", "result.json"),
            PredictionOutputPath = stagedDll
        };
        ChildProcessResult stageCollision = await RunHostAsync(fixture.Request, TestContext.Current.CancellationToken);
        HostResponse stageResponse = JsonSerializer.Deserialize<HostResponse>(stageCollision.StandardOutput, TestJson.Options)!;
        Assert.Equal("invalid_request", stageResponse.ErrorCode);
        Assert.Equal(originalDll, await File.ReadAllBytesAsync(stagedDll, TestContext.Current.CancellationToken));

        fixture.Request = fixture.Request with
        {
            PredictionOutputPath = Path.Combine(fixture.Request.NativeStageDirectory, "new-output.rttm")
        };
        ChildProcessResult stageDirectoryOutput = await RunHostAsync(fixture.Request, TestContext.Current.CancellationToken);
        HostResponse stageDirectoryResponse = JsonSerializer.Deserialize<HostResponse>(stageDirectoryOutput.StandardOutput, TestJson.Options)!;
        Assert.Equal("invalid_request", stageDirectoryResponse.ErrorCode);
        Assert.False(File.Exists(fixture.Request.PredictionOutputPath));
    }

    [Fact]
    public async Task UnmanifestedStageDllIsRejected()
    {
        using var fixture = SyntheticHostFixture.Create(NativeStageKind.Cpu);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Request.NativeStageDirectory, "unexpected-dependency.dll"),
            [1, 2, 3], TestContext.Current.CancellationToken);
        ChildProcessResult process = await RunHostAsync(fixture.Request, TestContext.Current.CancellationToken);
        HostResponse response = JsonSerializer.Deserialize<HostResponse>(process.StandardOutput, TestJson.Options)!;

        Assert.Equal(2, process.ExitCode);
        Assert.Equal("invalid_native_stage", response.ErrorCode);
        Assert.Contains("unmanifested DLL 'unexpected-dependency.dll'", response.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CudaPreflightRequiresDeviceIndexAndCudaRuntimeFiles()
    {
        using var fixture = SyntheticHostFixture.Create(NativeStageKind.Cuda);
        ChildProcessResult validProcess = await RunHostAsync(fixture.Request, TestContext.Current.CancellationToken);
        HostResponse validResponse = JsonSerializer.Deserialize<HostResponse>(validProcess.StandardOutput, TestJson.Options)!;
        Assert.Equal("ready", validResponse.Status);
        Assert.Equal(0, validResponse.RequestedGpuIndex);

        fixture.Request = fixture.Request with { GpuDeviceIndex = null };
        ChildProcessResult invalidProcess = await RunHostAsync(fixture.Request, TestContext.Current.CancellationToken);
        HostResponse invalidResponse = JsonSerializer.Deserialize<HostResponse>(invalidProcess.StandardOutput, TestJson.Options)!;
        Assert.Equal("invalid_request", invalidResponse.ErrorCode);
        Assert.Contains("require a nonnegative GPU", invalidResponse.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidAudioFormatIsRejectedDuringPreflight()
    {
        using var fixture = SyntheticHostFixture.Create(NativeStageKind.Cpu);
        fixture.Request = fixture.Request with { InputFormat = "mp3" };
        ChildProcessResult process = await RunHostAsync(fixture.Request, TestContext.Current.CancellationToken);
        HostResponse response = JsonSerializer.Deserialize<HostResponse>(process.StandardOutput, TestJson.Options)!;

        Assert.Equal("invalid_audio", response.ErrorCode);
        Assert.Contains("InputFormat", response.ErrorMessage, StringComparison.Ordinal);
    }

    private static async Task<ChildProcessResult> RunHostAsync(HostRequest request, CancellationToken cancellationToken) =>
        await RunRawHostRequestAsync(JsonSerializer.Serialize(request, TestJson.Options), cancellationToken);

    private static async Task<ChildProcessResult> RunRawHostRequestAsync(string json, CancellationToken cancellationToken)
    {
        string requestPath = Path.Combine(Path.GetTempPath(), $"native-host-request-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, json);
        try
        {
            string hostAssembly = HostAssemblyLocator.Find();
            ProcessStartInfo startInfo = ChildProcessRunner.CreateStartInfo(
                DotnetPath(), [hostAssembly, "--request", requestPath], Path.GetDirectoryName(hostAssembly)!);
            return await new ChildProcessRunner(new SystemChildProcessLauncher())
                .RunAsync(startInfo, TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally
        {
            File.Delete(requestPath);
        }
    }

    private static string DotnetPath() => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
}

internal static class TestJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}

internal sealed record HostRequest
{
    public int ProtocolVersion { get; init; } = 1;
    public string RequestId { get; init; } = "test-request";
    public string Mode { get; init; } = "preflight";
    public string Backend { get; init; } = "cpu";
    public int? GpuDeviceIndex { get; init; }
    public string NativeRuntimeIdentity { get; init; } = "nemo-speech-asr 0.2.0";
    public string NativeStageDirectory { get; init; } = string.Empty;
    public IReadOnlyList<NativeFileHash> NativeFiles { get; init; } = [];
    public string ModelPath { get; init; } = string.Empty;
    public string AudioPath { get; init; } = string.Empty;
    public string InputFormat { get; init; } = "pcmF32Le";
    public int SampleRate { get; init; } = 16000;
    public double AudioStartSeconds { get; init; }
    public double DurationSeconds { get; init; } = 8;
    public string RecordingId { get; init; } = "synthetic";
    public string PredictionOutputPath { get; init; } = string.Empty;
    public string ResultOutputPath { get; init; } = string.Empty;
}

internal sealed record NativeFileHash
{
    public string Name { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
}

internal sealed record HostResponse
{
    public int ProtocolVersion { get; init; }
    public string RequestId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
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
    public int SpeakerCount { get; init; }
    public string PredictionRttmPath { get; init; } = string.Empty;
}

internal static class HostAssemblyLocator
{
    public static string Find()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "NemotronDiarizationSharp.sln")))
            current = current.Parent;
        if (current is null)
            throw new DirectoryNotFoundException("Could not locate the repository solution from the integration test output directory.");

        string configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
        string path = Path.Combine(current.FullName, "tests", "NemotronDiarizationSharp.NativeTestHost",
            "bin", configuration, "net10.0", "NemotronDiarizationSharp.NativeTestHost.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException("Native test host has not been built.", path);
        return path;
    }
}

internal enum NativeStageKind { Cpu, Cuda }

internal sealed class SyntheticHostFixture : IDisposable
{
    private static readonly string[] CpuFiles =
    ["nemo_speech_asr_c.dll", "nemo_speech_asr.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll"];
    private static readonly string[] CudaFiles =
    ["ggml-cuda.dll", "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll"];

    private SyntheticHostFixture(string root, HostRequest request) { Root = root; Request = request; }
    public string Root { get; }
    public HostRequest Request { get; set; }

    public static SyntheticHostFixture Create(NativeStageKind kind, bool includeCudaFiles = false)
    {
        string root = Path.Combine(Path.GetTempPath(), $"native-host-fixture-{Guid.NewGuid():N}");
        string stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(stage);
        byte[] model = [7, 8, 9];
        string modelPath = Path.Combine(root, "model.gguf");
        File.WriteAllBytes(modelPath, model);
        string audioPath = Path.Combine(root, "smoke.raw");
        using (var audio = File.Create(audioPath))
            audio.SetLength(8L * 16000 * sizeof(float));
        var names = CpuFiles.Concat(kind == NativeStageKind.Cuda || includeCudaFiles ? CudaFiles : []).ToArray();
        var hashes = new List<NativeFileHash>();
        foreach (string name in names)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes($"synthetic {name}");
            string path = Path.Combine(stage, name);
            File.WriteAllBytes(path, bytes);
            hashes.Add(new NativeFileHash { Name = name, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
        }
        var request = new HostRequest
        {
            Backend = kind == NativeStageKind.Cuda ? "cuda" : "cpu",
            GpuDeviceIndex = kind == NativeStageKind.Cuda ? 0 : null,
            NativeStageDirectory = stage,
            NativeFiles = hashes,
            ModelPath = modelPath,
            AudioPath = audioPath,
            PredictionOutputPath = Path.Combine(root, "out", "prediction.rttm"),
            ResultOutputPath = Path.Combine(root, "out", "result.json")
        };
        return new SyntheticHostFixture(root, request);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
