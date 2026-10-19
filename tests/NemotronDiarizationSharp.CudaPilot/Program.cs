using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NemotronDiarizationSharp;

namespace NemotronDiarizationSharp.CudaPilot;

internal static class Program
{
    private const string ExpectedRuntimeVersion = "nemo-speech-asr 0.2.0";
    private const int MinimumFreeMemoryMiB = 8192;
    private const int MaximumIdleUtilizationPercent = 80;

    private static readonly IReadOnlyDictionary<string, string> ExpectedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["nemo_speech_asr_c.dll"] = "54F2ADD01DCF59486633EE7E4B933180532F75A31B68266851D2C71939D99BEB",
        ["nemo_speech_asr.dll"] = "861BD97823B860A68BF5D209B763A7E63306F362C8BE76BEFC6770C8EE651E81",
        ["ggml.dll"] = "3648C7F2F358F327D2C8D2A4854F020B7DFE1BAD5260AFF6D532C7938E43AAEB",
        ["ggml-base.dll"] = "427C434FDBC3F9F8A697C333B940DD1EACC85D4A4D13BF5496ECBC3C7F177EF8",
        ["ggml-cpu.dll"] = "B942B9F3E37905A0355F6B118FD506E6969E636354EFF82691335146E87A65B6",
        ["ggml-cuda.dll"] = "D28232E4C4489AC1B9E37ACD70B4F064C3AAB0AB21B2539359918E6E6EA0B756",
        ["cudart64_12.dll"] = "760C38928BBE5759F7B31ED6692599EB7EC83CEDD5702E84C2B72028A89837E1",
        ["cublas64_12.dll"] = "90052A83EFD1B57A8E3616A6590B335855F81B814A4F16EECB7B5BF6D1B1D4EB",
        ["cublasLt64_12.dll"] = "C3A05EA244C937314AFEC09F87B91F814C7E27977681F6C67EB51BB06CED3A4A",
        ["msvcp140.dll"] = "7C26614E1D733892C2DEAC7E245CE115504B1D80592DD0A01B08E3E5A55F89CA",
        ["vcruntime140.dll"] = "D1F4225DF2CD877DBF130D5668A021DCE3F94118455FF5EC952061C30AFC9CE7",
        ["vcruntime140_1.dll"] = "A7146C08F89FE5B04541AB507CDB59FF7B44534D4BA3C668A426C6450A03434E",
        ["vcomp140.dll"] = "55ABA23CDCD6484FBB06F4155B8CA75ADFCE7A881F10AFD0C49457165E677164"
    };

    private static readonly string[] BackendDlls =
    [
        "nemo_speech_asr_c.dll", "nemo_speech_asr.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll", "ggml-cuda.dll"
    ];

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("The CUDA pilot requires a Windows x64 process.");

            string appDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            VerifyStagedNativeFiles(appDirectory);
            string nativeVersion = DiarizationModel.NativeRuntimeVersion;
            if (!string.Equals(nativeVersion, ExpectedRuntimeVersion, StringComparison.Ordinal))
                throw new InvalidOperationException($"Expected native version {ExpectedRuntimeVersion}, got {nativeVersion}.");

            IReadOnlyDictionary<string, string> loadedNativeModules = GetLoadedNativeModulePaths();
            VerifyLoadedNativeModules(appDirectory, loadedNativeModules);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                phase = "runtime-load-preflight",
                status = "PASS",
                nativeVersion
            }, new JsonSerializerOptions { WriteIndented = true }));

            if (!args.Contains("--run-smoke", StringComparer.Ordinal))
            {
                Console.WriteLine("Preflight only. Pass --run-smoke to run the opt-in 8-second streaming inference.");
                return 0;
            }

            string modelPath = ValueAfter(args, "--model") ?? DefaultFixturePath("Nemotron-3-Diarization.q8_0.gguf");
            string pcmPath = ValueAfter(args, "--pcm") ?? DefaultFixturePath("smoke-f32le.raw");
            VerifyFile(modelPath, 107_012_128, "08456D9E22CD9A323C0364D98375F3746D6E68507EBB705CD46438C534C7A3A1", "model");
            VerifyFile(pcmPath, 512_000, "23978983C7B52C95F8DABF7639FF64FE9629BD0D094CD208992EEDDFBE9A72D0", "PCM fixture");
            if (!BitConverter.IsLittleEndian)
                throw new PlatformNotSupportedException("The authorized fixture contains little-endian float32 PCM.");

            GpuSample gpuBefore = CaptureGpuSample();
            if (gpuBefore.FreeMiB < MinimumFreeMemoryMiB || gpuBefore.UtilizationPercent >= MaximumIdleUtilizationPercent)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    phase = "gpu-resource-preflight",
                    status = "REVIEW_REQUIRED",
                    gpuBefore,
                    requiredFreeMiB = MinimumFreeMemoryMiB,
                    maximumUtilizationPercent = MaximumIdleUtilizationPercent,
                    message = "Resources are too constrained for this smoke test; no inference was started."
                }, new JsonSerializerOptions { WriteIndented = true }));
                return 2;
            }

            var gpuSamples = new List<GpuSample> { gpuBefore };
            using var samplerCancellation = new CancellationTokenSource();
            Task sampler = SampleGpuUntilCancelledAsync(gpuSamples, samplerCancellation.Token);
            Stopwatch endToEnd = Stopwatch.StartNew();
            double modelLoadMilliseconds;
            double streamOpenMilliseconds;
            double pushFinishMilliseconds;
            double snapshotMilliseconds;
            DiarizationSnapshot snapshot;
            Environment.SetEnvironmentVariable("NEMO_SPEECH_MEMSTATS", "1");
            Environment.SetEnvironmentVariable("NEMO_SPEECH_SCHEDULE_CAPTURE", "1");
            using (DiarizationModel model = LoadGpuModel(modelPath, out modelLoadMilliseconds))
            {
                Stopwatch streamOpen = Stopwatch.StartNew();
                using (DiarizationStream stream = model.OpenStream())
                {
                    streamOpen.Stop();
                    streamOpenMilliseconds = streamOpen.Elapsed.TotalMilliseconds;

                    byte[] pcmBytes = File.ReadAllBytes(pcmPath);
                    float[] pcm = new float[pcmBytes.Length / sizeof(float)];
                    Buffer.BlockCopy(pcmBytes, 0, pcm, 0, pcmBytes.Length);

                    Stopwatch pushFinish = Stopwatch.StartNew();
                    stream.Push(pcm, sampleRate: 16_000);
                    stream.Finish();
                    pushFinish.Stop();
                    pushFinishMilliseconds = pushFinish.Elapsed.TotalMilliseconds;

                    Stopwatch snapshotTimer = Stopwatch.StartNew();
                    snapshot = stream.GetSnapshot();
                    snapshotTimer.Stop();
                    snapshotMilliseconds = snapshotTimer.Elapsed.TotalMilliseconds;
                }
            }

            endToEnd.Stop();
            await Task.Delay(500).ConfigureAwait(false);
            samplerCancellation.Cancel();
            await sampler.ConfigureAwait(false);
            GpuSample gpuAfter = CaptureGpuSample();
            Process process = Process.GetCurrentProcess();
            ProcessMemoryCounters memory = GetProcessMemoryCounters(process);

            SnapshotValidation validation = ValidateSnapshot(snapshot);
            if (!validation.IsValid)
                throw new InvalidOperationException("Native output validation failed: " + string.Join("; ", validation.Errors));

            var result = new
            {
                phase = "cuda-streaming-smoke",
                status = "INFERENCE_COMPLETED",
                nativeVersion,
                modelSha256 = ExpectedHashesForFixture("model"),
                pcmSha256 = ExpectedHashesForFixture("pcm"),
                inputDurationSeconds = PcmBytesDuration(pcmPath),
                gpuDeviceIndex = 0,
                timingsMilliseconds = new
                {
                    modelLoad = modelLoadMilliseconds,
                    streamOpen = streamOpenMilliseconds,
                    pushAndFinish = pushFinishMilliseconds,
                    snapshotRetrieval = snapshotMilliseconds,
                    endToEnd = endToEnd.Elapsed.TotalMilliseconds
                },
                output = new
                {
                    snapshot.SpeakerCount,
                    snapshot.FrameCount,
                    snapshot.SecondsPerFrame,
                    snapshot.IsFinal,
                    probabilityMinimum = validation.ProbabilityMinimum,
                    probabilityMaximum = validation.ProbabilityMaximum,
                    snapshot.Segments,
                    cpuBaseline = new { speakerChannels = 8, frames = 801, secondsPerFrame = 0.010, speaker1Start = 0.85, speaker1End = 3.81 },
                    comparison = CompareCpuBaseline(snapshot)
                },
                processMemory = new
                {
                    peakWorkingSetBytes = process.PeakWorkingSet64,
                    peakPrivateBytes = (ulong)memory.PeakPagefileUsage,
                    privateBytesAfterCleanup = (ulong)memory.PrivateUsage
                },
                gpuMemoryAndUtilization = new
                {
                    before = gpuBefore,
                    maximumUsedMiBObserved = gpuSamples.Max(x => x.UsedMiB),
                    maximumUtilizationPercentObserved = gpuSamples.Max(x => x.UtilizationPercent),
                    samples = gpuSamples.Count,
                    afterCleanup = gpuAfter
                },
                cudaEvidence = new
                {
                    stagedCudaBackendLoaded = loadedNativeModules.ContainsKey("ggml-cuda.dll"),
                    cudaDriverLoadedFromSystem32 = IsSystemDriverModule(loadedNativeModules),
                    nativeMemstatsEnabled = true,
                    note = "Confirm direct CUDA computation from native lines showing GPU backend selection and CUDA scheduler/model buffers; module loading and nvidia-smi telemetry alone are indirect evidence."
                },
                cleanup = "Model and stream were disposed before post-run GPU sampling."
            };
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                phase = "cuda-pilot",
                status = "FAILED",
                exceptionType = exception.GetType().Name,
                message = "The diagnostic failed. Local exception details are intentionally omitted from output."
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private static DiarizationModel LoadGpuModel(string modelPath, out double elapsedMilliseconds)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        DiarizationModel model = DiarizationModel.Load(modelPath, new DiarizationModelOptions { GpuDeviceIndex = 0 });
        stopwatch.Stop();
        elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        return model;
    }

    private static void VerifyStagedNativeFiles(string appDirectory)
    {
        string[] foundBackendFiles = Directory.EnumerateFiles(appDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null && (name.StartsWith("nemo_speech_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase)))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] expectedBackendFiles = BackendDlls.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!foundBackendFiles.SequenceEqual(expectedBackendFiles, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unexpected staged NeMo/ggml DLL set: {string.Join(", ", foundBackendFiles)}.");

        string[] nestedBackendFiles = Directory.EnumerateFiles(appDirectory, "*.dll", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetDirectoryName(path), appDirectory, StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                string name = Path.GetFileName(path);
                return name.StartsWith("nemo_speech_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "llama.dll", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        if (nestedBackendFiles.Length != 0)
            throw new InvalidOperationException($"Unexpected nested NeMo/ggml runtime DLLs: {string.Join(", ", nestedBackendFiles)}.");

        foreach ((string name, string expectedHash) in ExpectedHashes)
        {
            string path = Path.Combine(appDirectory, name);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Required staged runtime dependency {name} is missing.", path);
            string actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Staged runtime hash mismatch for {name}: {actualHash}.");
        }

        if (File.Exists(Path.Combine(appDirectory, "nvcuda.dll")))
            throw new InvalidOperationException("The NVIDIA driver DLL must resolve from System32 and must not be staged beside the host.");
    }

    private static IReadOnlyDictionary<string, string> GetLoadedNativeModulePaths()
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            string name = module.ModuleName;
            if (BackendDlls.Contains(name, StringComparer.OrdinalIgnoreCase)
                || ExpectedHashes.ContainsKey(name)
                || string.Equals(name, "nvcuda.dll", StringComparison.OrdinalIgnoreCase))
                paths[name] = Path.GetFullPath(module.FileName);
        }
        return paths;
    }

    private static void VerifyLoadedNativeModules(string appDirectory, IReadOnlyDictionary<string, string> modules)
    {
        foreach (string name in ExpectedHashes.Keys)
        {
            if (!modules.TryGetValue(name, out string? modulePath))
                throw new DllNotFoundException($"Expected native dependency {name} was not loaded.");
            if (!string.Equals(Path.GetDirectoryName(modulePath), appDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Native dependency {name} loaded from an unexpected location: {modulePath}.");
        }

        if (!modules.TryGetValue("nvcuda.dll", out string? driverPath)
            || !string.Equals(Path.GetDirectoryName(driverPath), Environment.GetFolderPath(Environment.SpecialFolder.System), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("nvcuda.dll did not resolve from the installed Windows System32 driver directory.");
    }

    private static GpuSample CaptureGpuSample()
    {
        string output = RunNvidiaSmi("--query-gpu=memory.total,memory.free,memory.used,utilization.gpu --format=csv,noheader,nounits");
        string[] values = output.Trim().Split(',').Select(value => value.Trim()).ToArray();
        if (values.Length != 4 || values.Any(value => !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            throw new InvalidOperationException("Unable to parse the nvidia-smi GPU resource sample: " + output.Trim());
        return new GpuSample(int.Parse(values[0], CultureInfo.InvariantCulture), int.Parse(values[1], CultureInfo.InvariantCulture),
            int.Parse(values[2], CultureInfo.InvariantCulture), int.Parse(values[3], CultureInfo.InvariantCulture));
    }

    private static string RunNvidiaSmi(string arguments)
    {
        var startInfo = new ProcessStartInfo("nvidia-smi")
        {
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start nvidia-smi.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10_000) || process.ExitCode != 0)
            throw new InvalidOperationException($"nvidia-smi failed ({process.ExitCode}): {stderr}");
        return stdout;
    }

    private static async Task SampleGpuUntilCancelledAsync(List<GpuSample> samples, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                GpuSample sample = await Task.Run(CaptureGpuSample, cancellationToken).ConfigureAwait(false);
                lock (samples)
                    samples.Add(sample);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("GPU telemetry sample failed; exception details are omitted.");
            }

            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static SnapshotValidation ValidateSnapshot(DiarizationSnapshot snapshot)
    {
        var errors = new List<string>();
        if (snapshot.SpeakerCount != 8)
            errors.Add($"Expected eight speaker channels, received {snapshot.SpeakerCount}.");
        if (snapshot.FrameCount <= 0)
            errors.Add($"Invalid frame count {snapshot.FrameCount}.");
        if (!double.IsFinite(snapshot.SecondsPerFrame) || snapshot.SecondsPerFrame <= 0)
            errors.Add($"Invalid frame cadence {snapshot.SecondsPerFrame}.");
        if (!snapshot.IsFinal)
            errors.Add("The snapshot is not final after stream finish.");

        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        for (long frame = 0; frame < snapshot.FrameCount; frame++)
        {
            for (int speaker = 0; speaker < snapshot.SpeakerCount; speaker++)
            {
                float probability = snapshot.GetProbability(frame, speaker);
                if (!float.IsFinite(probability))
                    errors.Add($"Non-finite probability at frame {frame}, speaker {speaker}.");
                minimum = Math.Min(minimum, probability);
                maximum = Math.Max(maximum, probability);
            }
        }

        foreach (DiarizationSegment segment in snapshot.Segments)
        {
            if (!double.IsFinite(segment.StartTime) || !double.IsFinite(segment.EndTime)
                || segment.StartTime < 0 || segment.EndTime <= segment.StartTime
                || segment.EndTime > snapshot.FrameCount * snapshot.SecondsPerFrame + snapshot.SecondsPerFrame
                || segment.Speaker < 1 || segment.Speaker > snapshot.SpeakerCount)
                errors.Add($"Invalid segment {segment}.");
        }

        return new SnapshotValidation(errors.Count == 0, errors, minimum, maximum);
    }

    private static object CompareCpuBaseline(DiarizationSnapshot snapshot)
    {
        DiarizationSegment? speakerOne = snapshot.Segments.FirstOrDefault(segment => segment.Speaker == 1);
        return new
        {
            speakerChannelsMatch = snapshot.SpeakerCount == 8,
            frameCountDifference = snapshot.FrameCount - 801,
            cadenceDifferenceSeconds = snapshot.SecondsPerFrame - 0.010,
            speakerOneSegment = speakerOne,
            comparedToCpuSpeakerOne = speakerOne is null
                ? null
                : new { startDifferenceSeconds = speakerOne.StartTime - 0.85, endDifferenceSeconds = speakerOne.EndTime - 3.81 }
        };
    }

    private static string DefaultFixturePath(string fileName)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "NemotronSharp-WindowsCpuPoC", fileName);

    private static string? ValueAfter(string[] args, string option)
    {
        int index = Array.IndexOf(args, option);
        return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
    }

    private static void VerifyFile(string path, long expectedLength, string expectedHash, string label)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedLength)
            throw new InvalidDataException($"{label} fixture is missing or has unexpected length: {path}.");
        string actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} fixture SHA-256 mismatch: {actualHash}.");
    }

    private static string ExpectedHashesForFixture(string fixture)
        => fixture switch
        {
            "model" => "08456D9E22CD9A323C0364D98375F3746D6E68507EBB705CD46438C534C7A3A1",
            "pcm" => "23978983C7B52C95F8DABF7639FF64FE9629BD0D094CD208992EEDDFBE9A72D0",
            _ => throw new ArgumentOutOfRangeException(nameof(fixture))
        };

    private static double PcmBytesDuration(string path) => new FileInfo(path).Length / (16_000d * sizeof(float));

    private static bool IsSystemDriverModule(IReadOnlyDictionary<string, string> modules)
        => modules.TryGetValue("nvcuda.dll", out string? path)
            && string.Equals(Path.GetDirectoryName(path), Environment.GetFolderPath(Environment.SpecialFolder.System), StringComparison.OrdinalIgnoreCase);

    private static ProcessMemoryCounters GetProcessMemoryCounters(Process process)
    {
        var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        if (!GetProcessMemoryInfo(process.Handle, ref counters, counters.Size))
            throw new InvalidOperationException("GetProcessMemoryInfo failed: " + Marshal.GetLastWin32Error());
        return counters;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCounters counters, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    private sealed record GpuSample(int TotalMiB, int FreeMiB, int UsedMiB, int UtilizationPercent);
    private sealed record SnapshotValidation(bool IsValid, IReadOnlyList<string> Errors, float ProbabilityMinimum, float ProbabilityMaximum);
}
