using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.ComponentModel;
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
    private sealed record ModuleObservation(
        DateTimeOffset ObservedAtUtc,
        int ProcessId,
        string ProcessArchitecture,
        bool EnumerationSucceeded,
        int TotalModuleCount,
        IReadOnlyList<ObservedModule> Modules,
        string? ExceptionType,
        string? ExceptionMessage);
    private sealed record ObservedModule(string Name, string FullPath);
    private sealed record NativeModuleCapture(int ProcessId, int SnapshotCount, TimeSpan Duration,
        IReadOnlyDictionary<string, string> ObservedModules, IReadOnlyList<ModuleObservation> Snapshots,
        bool ProcessExitedBeforeFirstSnapshot);
    private sealed record NativeExecutionTrace(int ProcessId, int? ExitCode, string StandardOutput, string StandardError,
        NativeModuleCapture ModuleCapture);
    private sealed record ExternalCrtModuleEvidence(string Name, string Path, string FileVersion, string PeMachine);
    private sealed record CpuSmokeEvidence(
        string StageDirectory,
        IReadOnlyList<NativeFileHash> StagedFiles,
        int NativeTestHostProcessId,
        int ModuleSnapshotCount,
        double ModuleCaptureMilliseconds,
        int? NativeTestHostExitCode,
        string NativeTestHostJson,
        string NativeTestHostStandardError,
        IReadOnlyDictionary<string, string> ObservedNativeModules,
        IReadOnlyList<ExternalCrtModuleEvidence> ObservedCrtModules,
        IReadOnlyDictionary<string, string>? HostReportedNativeModules,
        IReadOnlyList<ExternalCrtModuleEvidence>? ValidatedExternalCrtModules,
        IReadOnlyList<ModuleObservation> ParentModuleSnapshots,
        ModuleObservation? ChildModuleObservation);

    private static readonly string[] CpuEngineDlls =
    [
        "nemo_speech_asr_c.dll", "nemo_speech_asr.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll"
    ];

    private static readonly string[] ExternalCrtDlls =
    [
        "MSVCP140.dll", "VCRUNTIME140.dll", "VCRUNTIME140_1.dll", "VCOMP140.dll"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact(Explicit = true)]
    public async Task EightSecondCpuSmoke()
    {
        Assert.True(Environment.Is64BitProcess, "The explicit CPU smoke requires a Windows x64 test process.");
        using CpuNativeStage stage = CreateCpuNativeStage();

        NativeRequest request = CreateRequest("cpu", NativeMode.Inference,
            stage.NativeDirectory,
            stage.ManifestPath,
            RequiredPath("NEMOTONDIARIZATIONSHARP_MODEL"),
            RequiredPath("NEMOTONDIARIZATIONSHARP_A3_SMOKE_PCM_F32LE"), "pcmF32Le", 0, 8, "smoke");
        IReadOnlyDictionary<string, string> observedModules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        NativeExecutionTrace? executionTrace = null;
        string evidencePath = request.ResultOutputPath + ".a7_2cR-evidence.json";
        CpuSmokeEvidence? evidence = null;
        HostResponse response = await ExecuteInferenceAsync(request, async trace =>
        {
            executionTrace = trace;
            observedModules = trace.ModuleCapture.ObservedModules;
            evidence = new CpuSmokeEvidence(
                stage.NativeDirectory,
                stage.Files,
                trace.ProcessId,
                trace.ModuleCapture.SnapshotCount,
                trace.ModuleCapture.Duration.TotalMilliseconds,
                trace.ExitCode,
                trace.StandardOutput,
                trace.StandardError,
                trace.ModuleCapture.ObservedModules,
                GetObservedCrtModuleEvidence(trace.ModuleCapture.ObservedModules),
                HostReportedNativeModules: null,
                ValidatedExternalCrtModules: null,
                trace.ModuleCapture.Snapshots,
                ChildModuleObservation: null);
            await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, JsonOptions),
                TestContext.Current.CancellationToken);
        });
        Assert.NotNull(executionTrace);
        ModuleObservation childObservation = ReadChildModuleObservation(executionTrace!.StandardOutput);
        evidence = evidence! with
        {
            HostReportedNativeModules = response.LoadedNativeModules,
            ChildModuleObservation = childObservation
        };
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, JsonOptions),
            TestContext.Current.CancellationToken);
        AssertChildModuleObservation(childObservation, executionTrace.ProcessId, request);
        AssertCompleted(response, "cpu", expectedGpuIndex: -1, request);
        ExternalCrtModuleEvidence[] crtModules = AssertExternalCrtModules(response, request);
        evidence = evidence with { ValidatedExternalCrtModules = crtModules };
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, JsonOptions),
            TestContext.Current.CancellationToken);
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

    private async Task CompareAmiExcerptAsync(string recordingId, double startSeconds, string audioPath, string referencePath)
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

    private async Task<HostResponse> ExecuteInferenceAsync(
        NativeRequest request,
        Func<NativeExecutionTrace, Task>? onExecutionTrace = null)
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
            startInfo.Environment["NEMOTONDIARIZATIONSHARP_NATIVE_DIR"] = Path.GetFullPath(request.NativeStageDirectory);
            startInfo.Environment.Remove("NEMOTONSHARP_NATIVE_DIR");

            ChildProcessResult process;
            if (onExecutionTrace is null)
            {
                process = await new ChildProcessRunner(new SystemChildProcessLauncher())
                    .RunAsync(startInfo, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
            }
            else
            {
                (ChildProcessResult result, NativeExecutionTrace trace) = await RunNativeHostWithModuleCaptureAsync(
                    startInfo, TestContext.Current.CancellationToken);
                process = result;
                await onExecutionTrace(trace);
            }
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

    private static async Task<(ChildProcessResult Result, NativeExecutionTrace Trace)> RunNativeHostWithModuleCaptureAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        Process child;
        try
        {
            child = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The NativeTestHost process could not be started.");
        }
        catch (Exception exception)
        {
            return (new ChildProcessResult(false, null, false, false, string.Empty, string.Empty, exception.Message),
                new NativeExecutionTrace(0, null, string.Empty, string.Empty,
                    new NativeModuleCapture(0, 0, TimeSpan.Zero,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), [], false)));
        }

        using (child)
        using (var timeoutSource = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
        using (var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token))
        using (var captureCancellation = new CancellationTokenSource())
        {
            int processId = child.Id;
            Task<NativeModuleCapture> moduleCapture = CaptureRelevantNativeModulesUntilExitAsync(
                processId, captureCancellation.Token);
            Task<string> stdoutTask = child.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = child.StandardError.ReadToEndAsync(cancellationToken);
            bool timedOut = false;
            bool cancelled = false;
            try
            {
                await child.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = cancellationToken.IsCancellationRequested;
                timedOut = !cancelled && timeoutSource.IsCancellationRequested;
                if (!child.HasExited)
                {
                    try { child.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (child.HasExited) { }
                }

                try
                {
                    await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
                {
                    captureCancellation.Cancel();
                    NativeModuleCapture failedCapture = await moduleCapture.ConfigureAwait(false);
                    var failedTrace = new NativeExecutionTrace(processId, null, string.Empty, string.Empty, failedCapture);
                    return (new ChildProcessResult(true, null, timedOut, cancelled, string.Empty, string.Empty,
                        "Launched child process did not exit after process-tree termination."), failedTrace);
                }
            }

            string stdout = await ReadAfterExitAsync(stdoutTask).ConfigureAwait(false);
            string stderr = await ReadAfterExitAsync(stderrTask).ConfigureAwait(false);
            captureCancellation.Cancel();
            NativeModuleCapture observed = await moduleCapture.ConfigureAwait(false);
            var result = new ChildProcessResult(true, child.HasExited ? child.ExitCode : null,
                timedOut, cancelled, stdout, stderr, string.Empty);
            return (result, new NativeExecutionTrace(processId, result.ExitCode, stdout, stderr, observed));
        }
    }

    private static async Task<NativeModuleCapture> CaptureRelevantNativeModulesUntilExitAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var snapshots = new List<ModuleObservation>();
        var timer = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (Exception exception)
        {
            DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
            snapshots.Add(new ModuleObservation(observedAtUtc, processId, "UNKNOWN", false, -1, [],
                exception.GetType().FullName, exception.Message));
            timer.Stop();
            return new NativeModuleCapture(processId, snapshots.Count, timer.Elapsed, paths, snapshots,
                exception is ArgumentException);
        }

        using (process)
        {
        bool processExitedBeforeFirstSnapshot = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
            try
            {
                process.Refresh();
                if (process.HasExited)
                {
                    processExitedBeforeFirstSnapshot = snapshots.Count == 0;
                    break;
                }

                process.Refresh();
                string processArchitecture = GetProcessArchitecture(process);
                ObservedModule[] modules = process.Modules.Cast<ProcessModule>()
                    .Select(static module => new ObservedModule(module.ModuleName, Path.GetFullPath(module.FileName)))
                    .ToArray();
                snapshots.Add(new ModuleObservation(observedAtUtc, processId,
                    processArchitecture, true, modules.Length, modules, null, null));
                foreach (ObservedModule module in modules)
                {
                    if (IsRelevantNativeModule(module.Name))
                        paths[module.Name] = module.FullPath;
                }
            }
            catch (Exception exception)
            {
                bool hasExited;
                try
                {
                    process.Refresh();
                    hasExited = process.HasExited;
                }
                catch (Exception) { hasExited = true; }
                snapshots.Add(new ModuleObservation(observedAtUtc, processId,
                    "UNKNOWN", false, -1, [],
                    exception.GetType().FullName, exception.Message));
                if (hasExited)
                {
                    processExitedBeforeFirstSnapshot = snapshots.Count == 1;
                    break;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
        timer.Stop();
        return new NativeModuleCapture(processId, snapshots.Count, timer.Elapsed, paths, snapshots,
            processExitedBeforeFirstSnapshot);
        }
    }

    private static string GetProcessArchitecture(Process process)
    {
        string? executablePath = process.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executablePath))
            return "UNKNOWN";
        return ReadPeMachine(executablePath) switch
        {
            0x014C => "X86",
            0x8664 => "X64",
            0x01C4 => "ARM",
            0xAA64 => "ARM64",
            ushort machine => $"0x{machine:X4}"
        };
    }

    private static ModuleObservation ReadChildModuleObservation(string standardOutput)
    {
        using JsonDocument document = JsonDocument.Parse(standardOutput);
        JsonElement observation = document.RootElement.GetProperty("moduleObservation");
        return JsonSerializer.Deserialize<ModuleObservation>(observation.GetRawText(), JsonOptions)
            ?? throw new InvalidDataException("NativeTestHost returned no module-observation object.");
    }

    private static void AssertChildModuleObservation(
        ModuleObservation observation,
        int expectedProcessId,
        NativeRequest request)
    {
        Assert.True(observation.EnumerationSucceeded,
            $"REVIEW_REQUIRED: child module enumeration failed ({observation.ExceptionType}): {observation.ExceptionMessage}");
        Assert.True(observation.ProcessId == expectedProcessId,
            $"REVIEW_REQUIRED: child module observation PID {observation.ProcessId} does not match launched PID {expectedProcessId}.");
        Assert.True(observation.ProcessArchitecture == "X64",
            $"REVIEW_REQUIRED: child module observation architecture was '{observation.ProcessArchitecture}', expected X64.");
        Assert.True(observation.ObservedAtUtc > DateTimeOffset.MinValue,
            "REVIEW_REQUIRED: child module observation has no valid timestamp.");
        Assert.True(observation.TotalModuleCount > 0,
            "REVIEW_REQUIRED: child module observation returned no modules.");
        Assert.True(observation.TotalModuleCount == observation.Modules.Count,
            $"REVIEW_REQUIRED: child module observation count {observation.TotalModuleCount} does not match recorded entries {observation.Modules.Count}.");
        Assert.All(observation.Modules, module =>
        {
            Assert.False(string.IsNullOrWhiteSpace(module.Name));
            Assert.True(Path.IsPathFullyQualified(module.FullPath),
                $"REVIEW_REQUIRED: child module '{module.Name}' has no full path.");
        });
        Assert.True(observation.Modules.Any(module =>
                module.Name.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase)),
            "REVIEW_REQUIRED: child module observation omitted ntdll.dll.");
        Assert.True(observation.Modules.Any(module =>
            module.Name.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase)
            || module.Name.Equals("kernelbase.dll", StringComparison.OrdinalIgnoreCase)),
            "REVIEW_REQUIRED: child module observation omitted kernel32.dll and kernelbase.dll.");

        var expectedNativeNames = new HashSet<string>(CpuEngineDlls.Concat(ExternalCrtDlls),
            StringComparer.OrdinalIgnoreCase);
        ObservedModule[] relevantModules = observation.Modules
            .Where(module => IsRelevantNativeModule(module.Name))
            .ToArray();
        string[] unexpectedModules = relevantModules
            .Where(module => !expectedNativeNames.Contains(module.Name))
            .Select(module => $"{module.Name} at {module.FullPath}")
            .ToArray();
        Assert.True(unexpectedModules.Length == 0,
            $"REVIEW_REQUIRED: unexpected native modules were reported: {string.Join("; ", unexpectedModules)}");

        string stageDirectory = Path.GetFullPath(request.NativeStageDirectory);
        string systemDirectory = Path.GetFullPath(Environment.SystemDirectory);
        foreach (string name in CpuEngineDlls.Concat(ExternalCrtDlls))
        {
            ObservedModule[] matches = relevantModules
                .Where(module => string.Equals(module.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.True(matches.Length == 1,
                $"REVIEW_REQUIRED: expected exactly one child observation for '{name}', found {matches.Length}.");
            string expectedDirectory = ExternalCrtDlls.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? systemDirectory
                : stageDirectory;
            string expectedPath = Path.GetFullPath(Path.Combine(expectedDirectory, name));
            Assert.True(string.Equals(Path.GetFullPath(matches[0].FullPath), expectedPath,
                    StringComparison.OrdinalIgnoreCase),
                $"REVIEW_REQUIRED: child observed '{name}' at '{matches[0].FullPath}', expected '{expectedPath}'.");
        }
    }

    private static async Task<string> ReadAfterExitAsync(Task<string> readTask)
    {
        try { return await readTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { return string.Empty; }
        catch (OperationCanceledException) { return string.Empty; }
    }

    private static bool IsRelevantNativeModule(string name)
    {
        return name.StartsWith("nemo_speech_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase)
            || name.Equals("llama.dll", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("msvcp140", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("vcruntime140", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vcomp140.dll", StringComparison.OrdinalIgnoreCase)
            || name.Equals("concrt140.dll", StringComparison.OrdinalIgnoreCase)
            || name.Contains("cuda", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("cublas", StringComparison.OrdinalIgnoreCase);
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
            bool isExternalCrt = ExternalCrtDlls.Contains(moduleName, StringComparer.OrdinalIgnoreCase);
            string expectedDirectory = string.Equals(moduleName, "nvcuda.dll", StringComparison.OrdinalIgnoreCase)
                || isExternalCrt
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

    private ExternalCrtModuleEvidence[] AssertExternalCrtModules(HostResponse response, NativeRequest request)
    {
        string hostDirectory = Path.GetDirectoryName(HostAssemblyLocator.Find())!;
        string trustedSystemDirectory = Path.GetFullPath(Environment.SystemDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string stageDirectory = Path.GetFullPath(request.NativeStageDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var expectedNames = new HashSet<string>(CpuEngineDlls.Concat(ExternalCrtDlls), StringComparer.OrdinalIgnoreCase);
        string[] unexpectedNativeModules = response.LoadedNativeModules.Keys
            .Where(IsRelevantNativeModule)
            .Where(name => !expectedNames.Contains(name))
            .ToArray();
        Assert.Empty(unexpectedNativeModules);

        var crtModules = new List<ExternalCrtModuleEvidence>(ExternalCrtDlls.Length);
        foreach (string name in ExternalCrtDlls)
        {
            string appLocalPath = Path.Combine(hostDirectory, name);
            Assert.False(File.Exists(appLocalPath),
                $"Unexpected app-local CRT copy found at '{appLocalPath}'. The test will not silently accept it.");

            string? loadedPath = response.LoadedNativeModules
                .Where(entry => string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                .Select(static entry => entry.Value)
                .FirstOrDefault();
            Assert.False(string.IsNullOrWhiteSpace(loadedPath),
                $"REVIEW_REQUIRED: required CRT module '{name}' was not observed in the NativeTestHost process.");

            string fullPath = Path.GetFullPath(loadedPath!);
            string directory = Path.GetDirectoryName(fullPath)!
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Assert.False(IsWithinDirectory(stageDirectory, fullPath),
                $"CRT module '{name}' loaded from the selected five-DLL stage: '{fullPath}'.");
            Assert.False(IsWithinDirectory(hostDirectory, fullPath),
                $"CRT module '{name}' loaded from the NativeTestHost application directory: '{fullPath}'.");
            Assert.True(string.Equals(directory, trustedSystemDirectory, StringComparison.OrdinalIgnoreCase),
                $"REVIEW_REQUIRED: CRT module '{name}' loaded from unverified external location '{fullPath}'. " +
                $"The trusted Windows system directory for this process is '{trustedSystemDirectory}'.");
            Assert.True(File.Exists(fullPath), $"Loaded CRT module path no longer exists: '{fullPath}'.");
            Assert.Equal((ushort)0x8664, ReadPeMachine(fullPath));

            string version = FileVersionInfo.GetVersionInfo(fullPath).FileVersion ?? "UNKNOWN";
            crtModules.Add(new ExternalCrtModuleEvidence(name, fullPath, version, "AMD64"));
        }
        return crtModules.ToArray();
    }

    private static ExternalCrtModuleEvidence[] GetObservedCrtModuleEvidence(
        IReadOnlyDictionary<string, string> observedModules)
    {
        var evidence = new List<ExternalCrtModuleEvidence>();
        foreach (string name in ExternalCrtDlls)
        {
            string? path = observedModules
                .Where(entry => string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                .Select(static entry => entry.Value)
                .FirstOrDefault();
            if (path is null)
                continue;

            string fullPath = Path.GetFullPath(path);
            string version = File.Exists(fullPath)
                ? FileVersionInfo.GetVersionInfo(fullPath).FileVersion ?? "UNKNOWN"
                : "FILE_UNAVAILABLE";
            string machine = "UNKNOWN";
            if (File.Exists(fullPath))
            {
                ushort peMachine = ReadPeMachine(fullPath);
                machine = peMachine == 0x8664 ? "AMD64" : $"0x{peMachine:X4}";
            }
            evidence.Add(new ExternalCrtModuleEvidence(name, fullPath, version, machine));
        }
        return evidence.ToArray();
    }

    private static CpuNativeStage CreateCpuNativeStage()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourceDirectory = Path.Combine(repositoryRoot,
            "src", "NemotronDiarizationSharp.Runtime.Cpu", "runtimes", "win-x64", "native");
        string cpuManifestPath = Path.Combine(repositoryRoot,
            "src", "NemotronDiarizationSharp.Runtime.Cpu", "cpu-runtime-manifest.json");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(cpuManifestPath));
        JsonElement records = manifest.RootElement.GetProperty("files");
        string rootDirectory = Path.Combine(Path.GetTempPath(), "NemotronDiarizationSharp", "A7.2cR", Guid.NewGuid().ToString("N"));
        string stageDirectory = Path.Combine(rootDirectory, "native");
        string runtimeManifestPath = Path.Combine(rootDirectory, "runtime-manifest.json");
        Directory.CreateDirectory(stageDirectory);

        try
        {
            var stagedFiles = new List<NativeFileHash>(CpuEngineDlls.Length);
            foreach (string name in CpuEngineDlls)
            {
                JsonElement[] matches = records.EnumerateArray()
                    .Where(record => string.Equals(record.GetProperty("fileName").GetString(), name, StringComparison.Ordinal))
                    .ToArray();
                Assert.Single(matches);
                JsonElement record = matches[0];
                Assert.Equal("ENGINE", record.GetProperty("classification").GetString());
                string expectedHash = record.GetProperty("sha256").GetString()
                    ?? throw new InvalidDataException($"CPU manifest record for '{name}' has no SHA-256.");
                long expectedSize = record.GetProperty("sizeBytes").GetInt64();

                string sourcePath = Path.Combine(sourceDirectory, name);
                VerifyManifestFile(sourcePath, expectedSize, expectedHash, name);
                string stagedPath = Path.Combine(stageDirectory, name);
                File.Copy(sourcePath, stagedPath);
                VerifyManifestFile(stagedPath, expectedSize, expectedHash, name);
                stagedFiles.Add(new NativeFileHash { Name = name, Sha256 = expectedHash });
            }

            string[] stagedDlls = Directory.EnumerateFiles(stageDirectory, "*.dll", SearchOption.AllDirectories)
                .Select(static path => Path.GetFileName(path)!)
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] expectedNames = CpuEngineDlls.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            Assert.Equal(expectedNames, stagedDlls);

            var runtimeManifest = new RuntimeManifest
            {
                Backend = "cpu",
                NativeRuntimeIdentity = "nemo-speech-asr 0.2.0",
                Files = stagedFiles
            };
            File.WriteAllText(runtimeManifestPath, JsonSerializer.Serialize(runtimeManifest, JsonOptions));
            return new CpuNativeStage(rootDirectory, stageDirectory, runtimeManifestPath, stagedFiles);
        }
        catch
        {
            if (Directory.Exists(rootDirectory))
                Directory.Delete(rootDirectory, recursive: true);
            throw;
        }
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
        throw new DirectoryNotFoundException("Could not locate NemotronDiarizationSharp.sln from the integration test output directory.");
    }

    private static void VerifyManifestFile(string path, long expectedSize, string expectedHash, string name)
    {
        Assert.True(File.Exists(path), $"Manifest DLL '{name}' is missing at '{path}'.");
        using FileStream stream = File.OpenRead(path);
        Assert.Equal(expectedSize, stream.Length);
        string actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        Assert.Equal(expectedHash, actualHash, ignoreCase: true);
    }

    private static bool IsWithinDirectory(string directory, string filePath)
    {
        string fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(filePath);
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static ushort ReadPeMachine(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        Assert.Equal((ushort)0x5A4D, reader.ReadUInt16());
        stream.Position = 0x3C;
        int peOffset = reader.ReadInt32();
        Assert.True(peOffset >= 0x40 && peOffset <= stream.Length - 6, $"Invalid PE header offset {peOffset} in '{path}'.");
        stream.Position = peOffset;
        Assert.Equal(0x00004550u, reader.ReadUInt32());
        return reader.ReadUInt16();
    }

    private sealed class CpuNativeStage(string rootDirectory, string nativeDirectory, string manifestPath,
        IReadOnlyList<NativeFileHash> files) : IDisposable
    {
        public string NativeDirectory { get; } = nativeDirectory;
        public string ManifestPath { get; } = manifestPath;
        public IReadOnlyList<NativeFileHash> Files { get; } = files;

        public void Dispose()
        {
            if (Directory.Exists(rootDirectory))
                Directory.Delete(rootDirectory, recursive: true);
        }
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
