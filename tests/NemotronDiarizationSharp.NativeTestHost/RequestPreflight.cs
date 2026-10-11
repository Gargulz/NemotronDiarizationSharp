using System.Diagnostics;

namespace NemotronDiarizationSharp.NativeTestHost;

internal sealed record AudioInputDescription(long DataOffset, long TotalFrames, int SampleRate, int BytesPerSample);
internal sealed record PreflightResult(NativeTestResponse Response, AudioInputDescription? Audio);

internal static class RequestPreflight
{
    public static PreflightResult Validate(NativeTestRequest request)
    {
        var timer = Stopwatch.StartNew();
        NativeTestResponse Failure(string code, string message) => new()
        {
            RequestId = request.RequestId,
            Status = "failed",
            ErrorCode = code,
            ErrorMessage = message,
            SelectedBackend = request.Backend.ToString().ToLowerInvariant(),
            RequestedGpuIndex = request.GpuDeviceIndex,
            NativeRuntimeIdentity = request.NativeRuntimeIdentity,
            PreflightMilliseconds = timer.Elapsed.TotalMilliseconds,
            TotalMilliseconds = timer.Elapsed.TotalMilliseconds
        };

        if (!AreOutputPathsSafe(request))
            return new(Failure("invalid_request", "Output paths must be absolute, distinct, outside the native stage, and must not overwrite model or audio inputs."), null);

        if (request.ProtocolVersion != 1)
            return new(Failure("unsupported_protocol", "Only request protocol version 1 is supported."), null);
        if (!Enum.IsDefined(request.Backend) || request.Backend == NativeBackend.Unknown)
            return new(Failure("invalid_request", "Backend must be 'cpu' or 'cuda'."), null);
        if (!Enum.IsDefined(request.Mode) || request.Mode == NativeTestMode.Unknown)
            return new(Failure("invalid_request", "Mode must be 'preflight' or 'inference'."), null);
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Any(char.IsWhiteSpace))
            return new(Failure("invalid_request", "RequestId must be a nonempty token without whitespace."), null);
        if (request.Backend == NativeBackend.Cpu && request.GpuDeviceIndex is not null)
            return new(Failure("invalid_request", "CPU requests must not specify a GPU device index."), null);
        if (request.Backend == NativeBackend.Cuda && request.GpuDeviceIndex is null or < 0)
            return new(Failure("invalid_request", "CUDA requests require a nonnegative GPU device index."), null);
        if (!double.IsFinite(request.DurationSeconds) || request.DurationSeconds <= 0)
            return new(Failure("invalid_request", "DurationSeconds must be finite and greater than zero."), null);
        if (!double.IsFinite(request.AudioStartSeconds) || request.AudioStartSeconds < 0)
            return new(Failure("invalid_request", "AudioStartSeconds must be finite and nonnegative."), null);
        if (request.SampleRate is < 8000 or > 96000)
            return new(Failure("invalid_request", "SampleRate must be in [8000, 96000]."), null);
        if (string.IsNullOrWhiteSpace(request.RecordingId) || request.RecordingId.Any(char.IsWhiteSpace))
            return new(Failure("invalid_request", "RecordingId must be a nonempty token without whitespace."), null);

        foreach ((string label, string path) in new[]
                 { ("ModelPath", request.ModelPath), ("AudioPath", request.AudioPath),
                     ("PredictionOutputPath", request.PredictionOutputPath), ("ResultOutputPath", request.ResultOutputPath) })
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                return new(Failure("invalid_request", $"{label} must be an absolute path."), null);
        }

        string? stageError = NativeStageValidator.Validate(request);
        if (stageError is not null)
            return new(Failure(stageError.Contains("SHA-256", StringComparison.Ordinal)
                ? "native_hash_mismatch" : "invalid_native_stage", stageError), null);

        if (!File.Exists(request.ModelPath))
            return new(Failure("missing_fixture", $"Model file is missing: {request.ModelPath}"), null);
        if (!File.Exists(request.AudioPath))
            return new(Failure("missing_fixture", $"Audio file is missing: {request.AudioPath}"), null);

        AudioInputDescription audio;
        try
        {
            audio = PcmInputReader.Inspect(request);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            return new(Failure("invalid_audio", exception.Message), null);
        }

        long excerptFrames = checked((long)Math.Round(request.DurationSeconds * request.SampleRate,
            MidpointRounding.AwayFromZero));
        long startFrame = checked((long)Math.Round(request.AudioStartSeconds * request.SampleRate,
            MidpointRounding.AwayFromZero));
        if (excerptFrames <= 0 || startFrame < 0 || startFrame > audio.TotalFrames
            || excerptFrames > audio.TotalFrames - startFrame)
            return new(Failure("missing_fixture", "Requested audio excerpt exceeds the available input frames."), null);

        timer.Stop();
        return new(new NativeTestResponse
        {
            RequestId = request.RequestId,
            Status = request.Mode == NativeTestMode.Preflight ? "ready" : "validated",
            SelectedBackend = request.Backend.ToString().ToLowerInvariant(),
            RequestedGpuIndex = request.GpuDeviceIndex,
            NativeRuntimeIdentity = request.NativeRuntimeIdentity,
            NativeRuntimeLoaded = false,
            PreflightMilliseconds = timer.Elapsed.TotalMilliseconds,
            TotalMilliseconds = timer.Elapsed.TotalMilliseconds
        }, audio);
    }

    public static bool AreOutputPathsSafe(NativeTestRequest request)
    {
        string[] paths = [request.ModelPath, request.AudioPath, request.PredictionOutputPath, request.ResultOutputPath];
        if (paths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            return false;

        try
        {
            string model = Path.GetFullPath(request.ModelPath);
            string audio = Path.GetFullPath(request.AudioPath);
            string prediction = Path.GetFullPath(request.PredictionOutputPath);
            string result = Path.GetFullPath(request.ResultOutputPath);
            if (PathsEqual(prediction, result) || PathsEqual(prediction, model) || PathsEqual(result, model)
                || PathsEqual(prediction, audio) || PathsEqual(result, audio))
                return false;

            if (string.IsNullOrWhiteSpace(request.NativeStageDirectory)
                || !Path.IsPathFullyQualified(request.NativeStageDirectory))
                return true;

            string stage = Path.GetFullPath(request.NativeStageDirectory);
            return !IsWithinDirectory(prediction, stage) && !IsWithinDirectory(result, stage);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinDirectory(string path, string directory)
    {
        string relative = Path.GetRelativePath(directory, path);
        return relative == "." || (!Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
