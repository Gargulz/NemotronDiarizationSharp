using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NemotronDiarizationSharp.Evaluation;
using NemotronDiarizationSharp.NativeTestHost;
using NemotronDiarizationSharp;

if (args.Length != 2 || !string.Equals(args[0], "--request", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: NemotronDiarizationSharp.NativeTestHost --request <absolute-json-path>");
    return 64;
}

NativeTestRequest? request = null;
NativeTestResponse response;
try
{
    string requestPath = Path.GetFullPath(args[1]);
    request = JsonSerializer.Deserialize<NativeTestRequest>(await File.ReadAllTextAsync(requestPath), ProtocolJson.Options);
    if (request is null)
        throw new JsonException("Request JSON must contain an object.");
    PreflightResult validation = RequestPreflight.Validate(request);
    response = validation.Response;
    if (response.Status != "failed" && request.Mode == NativeTestMode.Inference)
        response = await RunInferenceAsync(request, validation.Audio!, response);
}
catch (JsonException exception)
{
    response = Failure(request, "invalid_request_json", exception.Message);
}
catch (Exception exception)
{
    response = Failure(request, "host_error", exception.Message);
}

if (request is not null && RequestPreflight.AreOutputPathsSafe(request))
{
    try
    {
        string fullResultPath = Path.GetFullPath(request.ResultOutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullResultPath)!);
        await File.WriteAllTextAsync(fullResultPath,
            JsonSerializer.Serialize(response, ProtocolJson.Options) + Environment.NewLine, Encoding.UTF8);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
    {
        response = response with
        {
            Status = "failed",
            ErrorCode = "result_write_failed",
            ErrorMessage = exception.Message
        };
    }
}

Console.Out.WriteLine(JsonSerializer.Serialize(response, ProtocolJson.Options));
return response.Status == "failed" ? 2 : 0;

static NativeTestResponse Failure(NativeTestRequest? request, string code, string message) => new()
{
    RequestId = request?.RequestId ?? string.Empty,
    SelectedBackend = request?.Backend.ToString().ToLowerInvariant() ?? string.Empty,
    RequestedGpuIndex = request?.GpuDeviceIndex,
    NativeRuntimeIdentity = request?.NativeRuntimeIdentity ?? string.Empty,
    Status = "failed",
    ErrorCode = code,
    ErrorMessage = message
};

static async Task<NativeTestResponse> RunInferenceAsync(
    NativeTestRequest request, AudioInputDescription audio, NativeTestResponse ready)
{
    var total = Stopwatch.StartNew();
    bool priorityApplied = false;
    string priorityName = "BelowNormal";
    try
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        priorityApplied = true;
    }
    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
    {
        priorityName = $"BelowNormalUnavailable: {exception.GetType().Name}";
    }
    Environment.SetEnvironmentVariable("NEMOTONDIARIZATIONSHARP_NATIVE_DIR", request.NativeStageDirectory);
    var options = new DiarizationModelOptions
    {
        Preset = "v3-streaming",
        // The SDK represents the native CPU sentinel -1 as null, then passes -1 to the C ABI.
        GpuDeviceIndex = request.Backend == NativeBackend.Cpu ? null : request.GpuDeviceIndex,
        SpeakerCacheFrames = 264,
        FifoFrames = 40,
        ChunkFrames = 340,
        RightContextFrames = 40,
        UpdatePeriodFrames = 300
    };

    double modelLoadMilliseconds;
    double streamOpenMilliseconds;
    double inferenceMilliseconds;
    string runtimeIdentity;
    DiarizationSnapshot snapshot;
    IReadOnlyDictionary<string, string> loadedNativeModules;
    var modelTimer = Stopwatch.StartNew();
    using (DiarizationModel model = DiarizationModel.Load(request.ModelPath, options))
    {
        modelTimer.Stop();
        runtimeIdentity = DiarizationModel.NativeRuntimeVersion;
        modelLoadMilliseconds = modelTimer.Elapsed.TotalMilliseconds;

        var openTimer = Stopwatch.StartNew();
        using (DiarizationStream stream = model.OpenStream())
        {
            openTimer.Stop();
            streamOpenMilliseconds = openTimer.Elapsed.TotalMilliseconds;

            long frameCount = checked((long)Math.Round(request.DurationSeconds * request.SampleRate,
                MidpointRounding.AwayFromZero));
            int chunkSize = Math.Min(request.SampleRate, 16_384);
            float[] samples = new float[chunkSize];
            long processed = 0;
            var inferenceTimer = Stopwatch.StartNew();
            using (PcmInputReader input = PcmInputReader.Open(request, audio))
            {
                while (processed < frameCount)
                {
                    int requested = (int)Math.Min(samples.Length, frameCount - processed);
                    int read = input.ReadSamples(samples, requested);
                    if (read != requested)
                        throw new EndOfStreamException($"Audio ended after {processed + read} of {frameCount} excerpt frames.");
                    stream.Push(samples.AsSpan(0, read), request.SampleRate);
                    processed += read;
                }
                stream.Finish();
            }
            inferenceTimer.Stop();
            inferenceMilliseconds = inferenceTimer.Elapsed.TotalMilliseconds;
            snapshot = stream.GetSnapshot();
            loadedNativeModules = GetLoadedNativeModulePaths(request.NativeFiles);
        }
    }

    float probabilityMinimum = float.PositiveInfinity;
    float probabilityMaximum = float.NegativeInfinity;
    bool probabilitiesValid = true;
    for (long frame = snapshot.FirstProbabilityFrame; frame < snapshot.FrameCount; frame++)
    {
        for (int speaker = 0; speaker < snapshot.SpeakerCount; speaker++)
        {
            float probability = snapshot.GetProbability(frame, speaker);
            probabilitiesValid &= float.IsFinite(probability) && probability is >= 0 and <= 1;
            probabilityMinimum = Math.Min(probabilityMinimum, probability);
            probabilityMaximum = Math.Max(probabilityMaximum, probability);
        }
    }

    var segments = snapshot.Segments
        .Where(segment => segment.EndTime > segment.StartTime)
        .Select(segment => new RttmSegment(request.RecordingId, "1", segment.StartTime,
            segment.EndTime - segment.StartTime, $"speaker_{segment.Speaker}"))
        .ToArray();
    string predictionPath = Path.GetFullPath(request.PredictionOutputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(predictionPath)!);
    await using (var writer = new StreamWriter(predictionPath, false, new UTF8Encoding(false)))
        RttmWriter.Write(writer, segments);

    total.Stop();
    return ready with
    {
        Status = "completed",
        NativeRuntimeIdentity = runtimeIdentity,
        NativeRuntimeLoaded = true,
        ModelLoadMilliseconds = modelLoadMilliseconds,
        StreamOpenMilliseconds = streamOpenMilliseconds,
        InferenceMilliseconds = inferenceMilliseconds,
        TotalMilliseconds = total.Elapsed.TotalMilliseconds,
        ProcessPriority = priorityName,
        ProcessPriorityApplied = priorityApplied,
        InferenceMeasurementScope = "Stream Push and Finish only; excludes model load, stream open, snapshot retrieval, and RTTM writing.",
        FrameCount = snapshot.FrameCount,
        FirstProbabilityFrame = snapshot.FirstProbabilityFrame,
        SpeakerCount = snapshot.SpeakerCount,
        SecondsPerFrame = snapshot.SecondsPerFrame,
        ProbabilityMinimum = probabilityMinimum,
        ProbabilityMaximum = probabilityMaximum,
        AllProbabilitiesFiniteAndBounded = probabilitiesValid,
        IsFinal = snapshot.IsFinal,
        NativeResourcesDisposed = true,
        Segments = snapshot.Segments
            .Select(static segment => new NativeSegmentResult(segment.StartTime, segment.EndTime, segment.Speaker))
            .ToArray(),
        LoadedNativeModules = loadedNativeModules,
        PredictionRttmPath = predictionPath
    };
}

static IReadOnlyDictionary<string, string> GetLoadedNativeModulePaths(IReadOnlyList<NativeFileHash> manifestFiles)
{
    var expectedNames = new HashSet<string>(manifestFiles.Select(static file => file.Name), StringComparer.OrdinalIgnoreCase);
    expectedNames.UnionWith(["ggml-cuda.dll", "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll"]);
    var modules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
    {
        if (expectedNames.Contains(module.ModuleName)
            || string.Equals(module.ModuleName, "nvcuda.dll", StringComparison.OrdinalIgnoreCase))
            modules[module.ModuleName] = Path.GetFullPath(module.FileName);
    }
    return modules;
}
