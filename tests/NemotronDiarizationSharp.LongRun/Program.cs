using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using NemotronDiarizationSharp;

namespace NemotronDiarizationSharp.LongRun;

internal static class Program
{
    private const long ExpectedModelBytes = 107_012_128;
    private const long ExpectedPcmBytes = 512_000;
    private const string ExpectedModelSha256 = "08456d9e22cd9a323c0364d98375f3746d6e68507ebb705cd46438c534c7a3a1";
    private const string ExpectedPcmSha256 = "23978983c7b52c95f8dabf7639ff64fe9629bd0d094cd208992eeddfbe9a72d0";
    private const int DefaultSampleRate = 16_000;

    private static int Main(string[] args)
    {
        try
        {
            RunOptions options = RunOptions.Parse(args);
            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            return Run(options);
        }
        catch (DiarizationFrameHistoryGapException exception)
        {
            Console.Error.WriteLine($"FRAME_HISTORY_GAP missing=[{exception.MissingStartFrame}," +
                                    $"{exception.MissingEndFrameExclusive}) earliest={exception.EarliestAvailableFrame}");
            Console.Error.WriteLine("REVIEW_REQUIRED: incremental frame continuity was lost.");
            return 4;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"VALIDATION_FAILED: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static int Run(RunOptions options)
    {
        string modelPath = RequiredEnvironmentPath("NEMOTONDIARIZATIONSHARP_MODEL", "NEMOTONSHARP_MODEL");
        string pcmPath = RequiredEnvironmentPath("NEMOTONDIARIZATIONSHARP_PCM_F32LE", "NEMOTONSHARP_PCM_F32LE");
        string nativeDirectory = RequiredEnvironmentPath("NEMOTONDIARIZATIONSHARP_NATIVE_DIR", "NEMOTONSHARP_NATIVE_DIR");

        VerifyFile(modelPath, ExpectedModelBytes, ExpectedModelSha256, "model");
        float[] fixture = LoadPcmFixture(pcmPath);
        Environment.SetEnvironmentVariable("NEMOTONDIARIZATIONSHARP_NATIVE_DIR", nativeDirectory);

        Console.WriteLine($"Fixture validation: model_bytes={ExpectedModelBytes} model_sha256={ExpectedModelSha256}");
        Console.WriteLine($"Fixture validation: pcm_bytes={ExpectedPcmBytes} pcm_sha256={ExpectedPcmSha256} samples={fixture.Length}");
        Console.WriteLine($"Configuration: target_minutes={options.DurationMinutes.ToString("0.###", CultureInfo.InvariantCulture)} " +
                          $"chunk_seconds={options.ChunkSeconds.ToString("0.###", CultureInfo.InvariantCulture)} " +
                          $"poll_seconds={options.PollSeconds.ToString("0.###", CultureInfo.InvariantCulture)} " +
                          $"diagnostic_interval_seconds={options.DiagnosticIntervalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} " +
                          $"max_runtime_minutes={options.MaxRuntimeMinutes.ToString("0.###", CultureInfo.InvariantCulture)} " +
                          $"require_compaction={options.RequireCompaction}");

        var startupTimer = Stopwatch.StartNew();
        using var model = DiarizationModel.Load(modelPath);
        string runtimeVersion = DiarizationModel.NativeRuntimeVersion;
        startupTimer.Stop();
        if (!runtimeVersion.Contains("0.2.0", StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected native runtime 0.2.0, got '{runtimeVersion}'.");

        Console.WriteLine($"Native runtime: {runtimeVersion}; speakers={model.SpeakerCount}; " +
                          $"seconds_per_frame={model.SecondsPerFrame.ToString("0.########", CultureInfo.InvariantCulture)}; " +
                          $"model_load_seconds={startupTimer.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");

        long requestedSamples = checked((long)Math.Ceiling(options.DurationMinutes * 60 * DefaultSampleRate));
        int chunkCapacity = checked((int)Math.Ceiling(options.ChunkSeconds * DefaultSampleRate));
        long expectedNextFrame = 0;
        long batchesConsumed = 0;
        long framesConsumed = 0;
        long producedSamples = 0;
        long fixtureOffset = 0;
        double inputSecondsAtLastPoll = 0;
        double inputSecondsAtLastSample = 0;
        double elapsedSecondsAtLastSample = 0;
        double? retainedStartBeforeCompaction = null;
        double? retainedStartAfterCompaction = null;
        long? segmentsBeforeCompaction = null;
        long? segmentsAtCompaction = null;
        long? compactionReferenceBoundaryFrame = null;
        SegmentSummary? finalizedSegmentReference = null;
        SegmentSummary? finalHistoricalSegmentSummary = null;
        bool completedTarget = false;
        string? stopReason = null;

        using var stream = model.OpenStream();
        var processingTimer = Stopwatch.StartNew();
        var nextChunk = new float[chunkCapacity];

        // An empty read must preserve the cursor at zero before the stream has input.
        ValidateEmptyRead(stream.ReadNewFrames(), expectedNextFrame, "initial");
        ValidateEmptyRead(stream.ReadNewFrames(), expectedNextFrame, "initial-repeat");
        DiarizationSnapshot initialSnapshot = stream.GetSnapshot();
        ValidateSnapshot(initialSnapshot, expectedNextFrame, model);
        retainedStartBeforeCompaction = initialSnapshot.FirstProbabilityFrame;
        segmentsBeforeCompaction = initialSnapshot.Segments.Count;
        Console.WriteLine(FormatSnapshot("initial", initialSnapshot, model.SecondsPerFrame));

        var sampler = new RunSampler(options.DiagnosticIntervalSeconds);
        while (producedSamples < requestedSamples)
        {
            if (processingTimer.Elapsed.TotalMinutes >= options.MaxRuntimeMinutes)
            {
                stopReason = "wall_time_limit";
                break;
            }

            long remaining = requestedSamples - producedSamples;
            int count = (int)Math.Min(nextChunk.Length, remaining);
            for (int i = 0; i < count; i++)
                nextChunk[i] = fixture[(int)((fixtureOffset + i) % fixture.Length)];

            stream.Push(nextChunk.AsSpan(0, count), DefaultSampleRate);
            producedSamples += count;
            fixtureOffset = (fixtureOffset + count) % fixture.Length;
            inputSecondsAtLastPoll += (double)count / DefaultSampleRate;

            if (inputSecondsAtLastPoll >= options.PollSeconds)
            {
                DiarizationFrameBatch batch = stream.ReadNewFrames();
                ValidateBatch(batch, expectedNextFrame, model);
                if (batch.FrameCount > 0)
                {
                    expectedNextFrame = checked(expectedNextFrame + batch.FrameCount);
                    framesConsumed = checked(framesConsumed + batch.FrameCount);
                    batchesConsumed++;
                }
                else
                {
                    ValidateEmptyRead(batch, expectedNextFrame, "poll");
                }
                inputSecondsAtLastPoll = 0;
            }

            GCMemoryInfo memoryInfo = GC.GetGCMemoryInfo();
            if (memoryInfo.HighMemoryLoadThresholdBytes > 0 &&
                memoryInfo.MemoryLoadBytes >= memoryInfo.HighMemoryLoadThresholdBytes)
            {
                stopReason = "gc_memory_pressure";
                break;
            }

            double inputSeconds = (double)producedSamples / DefaultSampleRate;
            if (sampler.IsDue(inputSeconds))
            {
                DiarizationSnapshot snapshot = stream.GetSnapshot();
                ValidateSnapshot(snapshot, expectedNextFrame, model);
                if (snapshot.FirstProbabilityFrame == 0)
                {
                    retainedStartBeforeCompaction = 0;
                    segmentsBeforeCompaction = snapshot.Segments.Count;
                }
                else if (retainedStartAfterCompaction is null)
                {
                    retainedStartAfterCompaction = snapshot.FirstProbabilityFrame;
                    segmentsAtCompaction = snapshot.Segments.Count;
                    compactionReferenceBoundaryFrame = snapshot.FirstProbabilityFrame;
                    finalizedSegmentReference = SummarizeHistoricalSegments(
                        snapshot, snapshot.FirstProbabilityFrame * snapshot.SecondsPerFrame);
                    Console.WriteLine(FormatHistoricalSummary("compaction-reference", finalizedSegmentReference));
                }

                double elapsedSeconds = processingTimer.Elapsed.TotalSeconds;
                double intervalInput = inputSeconds - inputSecondsAtLastSample;
                double intervalElapsed = elapsedSeconds - elapsedSecondsAtLastSample;
                PrintSample(snapshot, inputSeconds, elapsedSeconds, intervalInput, intervalElapsed,
                    framesConsumed, batchesConsumed);

                inputSecondsAtLastSample = inputSeconds;
                elapsedSecondsAtLastSample = elapsedSeconds;
            }
        }

        completedTarget = stopReason is null && producedSamples == requestedSamples;
        stream.Finish();
        DiarizationFrameBatch finalBatch = stream.ReadNewFrames();
        ValidateBatch(finalBatch, expectedNextFrame, model);
        if (finalBatch.FrameCount > 0)
        {
            expectedNextFrame = checked(expectedNextFrame + finalBatch.FrameCount);
            framesConsumed = checked(framesConsumed + finalBatch.FrameCount);
            batchesConsumed++;
        }
        else
        {
            ValidateEmptyRead(finalBatch, expectedNextFrame, "post-finish");
        }

        DiarizationSnapshot finalSnapshot = stream.GetSnapshot();
        ValidateSnapshot(finalSnapshot, expectedNextFrame, model);
        if (expectedNextFrame != finalSnapshot.FrameCount)
            throw new InvalidOperationException($"Final frame continuity ended at {expectedNextFrame}, " +
                                                $"but native frame_count is {finalSnapshot.FrameCount}.");
        ValidateEmptyRead(stream.ReadNewFrames(), expectedNextFrame, "final-repeat");

        if (compactionReferenceBoundaryFrame.HasValue)
        {
            finalHistoricalSegmentSummary = SummarizeHistoricalSegments(finalSnapshot,
                compactionReferenceBoundaryFrame.Value * finalSnapshot.SecondsPerFrame);
            if (finalizedSegmentReference != finalHistoricalSegmentSummary)
                throw new InvalidOperationException("Historical finalized segments changed after the compaction reference snapshot.");
        }

        processingTimer.Stop();
        ValidateFinalSnapshotProbabilities(finalSnapshot);
        stream.Dispose();
        model.Dispose();
        PrintSample(finalSnapshot, (double)producedSamples / DefaultSampleRate,
            processingTimer.Elapsed.TotalSeconds, 0, 0, framesConsumed, batchesConsumed, "final");
        Console.WriteLine($"FRAME_CONTINUITY expected_next={expectedNextFrame} final_frame_count={finalSnapshot.FrameCount} " +
                          $"consumed_frames={framesConsumed} batches={batchesConsumed} status=continuous");
        Console.WriteLine($"SEGMENTS final_count={finalSnapshot.Segments.Count} " +
                          $"before_compaction_count={segmentsBeforeCompaction?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} " +
                          $"at_compaction_count={segmentsAtCompaction?.ToString(CultureInfo.InvariantCulture) ?? "not_observed"} " +
                          $"historical_prefix_preserved={(finalizedSegmentReference is not null ? "yes" : "not_checked")}");
        Console.WriteLine($"COMPACTION occurred={(retainedStartAfterCompaction.HasValue ? "yes" : "no")} " +
                          $"first_retained_before={retainedStartBeforeCompaction?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} " +
                          $"first_retained_after={retainedStartAfterCompaction?.ToString(CultureInfo.InvariantCulture) ?? "not_observed"}");
        Console.WriteLine($"FINALIZATION finished=true stream_disposed=true model_disposed=true " +
                          $"stop_reason={stopReason ?? "target_reached"} runtime_seconds={processingTimer.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");

        if (!completedTarget)
        {
            Console.WriteLine("REVIEW_REQUIRED: target duration was not completed; compaction was not accepted as a pass.");
            return 3;
        }

        if (!retainedStartAfterCompaction.HasValue && options.RequireCompaction)
        {
            Console.WriteLine("REVIEW_REQUIRED: native compaction was not observed; retained-window start stayed at zero.");
            return 3;
        }

        Console.WriteLine(retainedStartAfterCompaction.HasValue
            ? "LONG_RUN_VALIDATION=PASS"
            : "LONG_RUN_PREFLIGHT=PASS (compaction was not required or evaluated)");
        return 0;
    }

    private static void PrintSample(DiarizationSnapshot snapshot, double inputSeconds,
        double elapsedSeconds, double intervalInputSeconds, double intervalElapsedSeconds,
        long framesConsumed, long batchesConsumed, string label = "sample")
    {
        Process process = Process.GetCurrentProcess();
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        double intervalThroughput = intervalElapsedSeconds > 0
            ? intervalInputSeconds / intervalElapsedSeconds
            : 0;
        double totalThroughput = elapsedSeconds > 0 ? inputSeconds / elapsedSeconds : 0;
        long retainedFrames = snapshot.FrameCount - snapshot.FirstProbabilityFrame;

        Console.WriteLine($"{label.ToUpperInvariant()} logical_s={inputSeconds.ToString("0.###", CultureInfo.InvariantCulture)} " +
                          $"elapsed_s={elapsedSeconds.ToString("0.###", CultureInfo.InvariantCulture)} " +
                          $"throughput_x={totalThroughput.ToString("0.###", CultureInfo.InvariantCulture)} " +
                          $"interval_throughput_x={intervalThroughput.ToString("0.###", CultureInfo.InvariantCulture)} " +
                          $"working_set_mib={ToMiB(process.WorkingSet64).ToString("0.##", CultureInfo.InvariantCulture)} " +
                          $"private_mib={ToMiB(process.PrivateMemorySize64).ToString("0.##", CultureInfo.InvariantCulture)} " +
                          $"managed_allocated_mib={ToMiB(GC.GetTotalAllocatedBytes(false)).ToString("0.##", CultureInfo.InvariantCulture)} " +
                          $"managed_heap_live_estimate_mib={ToMiB(GC.GetTotalMemory(false)).ToString("0.##", CultureInfo.InvariantCulture)} " +
                          $"native_total_frames={snapshot.FrameCount} retained_start={snapshot.FirstProbabilityFrame} " +
                          $"native_retained_frames={retainedFrames} consumed_frames={framesConsumed} batches={batchesConsumed} " +
                          $"segments={snapshot.Segments.Count}");
    }

    private static void ValidateBatch(DiarizationFrameBatch batch, long expectedNextFrame, DiarizationModel model)
    {
        if (batch.FrameCount < 0)
            throw new InvalidOperationException($"Negative batch frame count: {batch.FrameCount}.");
        if (batch.SpeakerCount != model.SpeakerCount)
            throw new InvalidOperationException($"Batch speaker count changed to {batch.SpeakerCount}.");
        if (!CadenceMatches(batch.SecondsPerFrame, model.SecondsPerFrame))
            throw new InvalidOperationException($"Batch cadence changed to {batch.SecondsPerFrame}.");
        if (batch.FirstFrameIndex != expectedNextFrame)
            throw new InvalidOperationException($"Unexpected frame range start {batch.FirstFrameIndex}; expected {expectedNextFrame}.");
        if (batch.Probabilities.Count != checked(batch.FrameCount * batch.SpeakerCount))
            throw new InvalidOperationException("Batch probability count does not match frame-major dimensions.");
        foreach (float probability in batch.Probabilities)
        {
            if (!float.IsFinite(probability) || probability < 0 || probability > 1)
                throw new InvalidOperationException($"Batch contains a probability outside finite [0, 1]: {probability}.");
        }
    }

    private static void ValidateFinalSnapshotProbabilities(DiarizationSnapshot snapshot)
    {
        long count = 0;
        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        bool valid = true;

        for (long frame = snapshot.FirstProbabilityFrame; frame < snapshot.FrameCount; frame++)
        {
            for (int speaker = 0; speaker < snapshot.SpeakerCount; speaker++)
            {
                float probability = snapshot.GetProbability(frame, speaker);
                count++;
                if (!float.IsFinite(probability))
                {
                    valid = false;
                    continue;
                }

                minimum = Math.Min(minimum, probability);
                maximum = Math.Max(maximum, probability);
                if (probability < 0 || probability > 1)
                    valid = false;
            }
        }

        string minimumText = count > 0 && minimum != float.PositiveInfinity
            ? minimum.ToString("R", CultureInfo.InvariantCulture)
            : "n/a";
        string maximumText = count > 0 && maximum != float.NegativeInfinity
            ? maximum.ToString("R", CultureInfo.InvariantCulture)
            : "n/a";
        Console.WriteLine($"FINAL_PROBABILITY_SCAN count={count} min={minimumText} max={maximumText} " +
                          $"range=finite_[0,1] status={(valid ? "PASS" : "FAIL")}");

        if (!valid)
            throw new InvalidOperationException("Final snapshot contains a probability outside finite [0, 1].");
    }

    private static void ValidateEmptyRead(DiarizationFrameBatch batch, long expectedNextFrame, string phase)
    {
        if (batch.FrameCount != 0 || batch.FirstFrameIndex != expectedNextFrame)
            throw new InvalidOperationException($"Empty read changed the cursor during {phase}: " +
                                                $"first={batch.FirstFrameIndex}, count={batch.FrameCount}, expected={expectedNextFrame}.");
    }

    private static void ValidateSnapshot(DiarizationSnapshot snapshot, long expectedNextFrame, DiarizationModel model)
    {
        if (snapshot.FirstProbabilityFrame < 0 || snapshot.FrameCount < snapshot.FirstProbabilityFrame)
            throw new InvalidOperationException("Snapshot contains invalid retained-window bounds.");
        if (snapshot.FrameCount < expectedNextFrame)
            throw new InvalidOperationException($"Native frame count {snapshot.FrameCount} moved behind cursor {expectedNextFrame}.");
        if (snapshot.SpeakerCount != model.SpeakerCount || !CadenceMatches(snapshot.SecondsPerFrame, model.SecondsPerFrame))
            throw new InvalidOperationException("Snapshot speaker count or frame cadence changed.");

        double previousStart = -1;
        foreach (DiarizationSegment segment in snapshot.Segments)
        {
            if (!double.IsFinite(segment.StartTime) || !double.IsFinite(segment.EndTime) ||
                segment.StartTime < 0 || segment.EndTime < segment.StartTime || segment.StartTime < previousStart ||
                segment.Speaker < 1 || segment.Speaker > model.SpeakerCount)
                throw new InvalidOperationException("Snapshot contains invalid or unordered segments.");
            previousStart = segment.StartTime;
        }
    }

    private static SegmentSummary SummarizeHistoricalSegments(DiarizationSnapshot snapshot, double boundarySeconds)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[sizeof(long) * 2 + sizeof(int)];
        int count = 0;
        foreach (DiarizationSegment segment in snapshot.Segments)
        {
            if (segment.EndTime > boundarySeconds)
                continue;

            BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(segment.StartTime));
            BinaryPrimitives.WriteInt64LittleEndian(bytes[sizeof(long)..], BitConverter.DoubleToInt64Bits(segment.EndTime));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(sizeof(long) * 2)..], segment.Speaker);
            hash.AppendData(bytes);
            count++;
        }

        return new SegmentSummary(count, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static string FormatHistoricalSummary(string label, SegmentSummary summary)
        => $"SEGMENT_HISTORY label={label} finalized_prefix_count={summary.Count} sha256={summary.Sha256}";

    private static string FormatSnapshot(string label, DiarizationSnapshot snapshot, double secondsPerFrame)
        => $"SNAPSHOT label={label} frame_count={snapshot.FrameCount} first_retained={snapshot.FirstProbabilityFrame} " +
           $"retained_frames={snapshot.FrameCount - snapshot.FirstProbabilityFrame} " +
           $"retained_start_seconds={(snapshot.FirstProbabilityFrame * secondsPerFrame).ToString("0.###", CultureInfo.InvariantCulture)} " +
           $"segments={snapshot.Segments.Count} finalized={snapshot.IsFinal}";

    private static bool CadenceMatches(double left, double right)
        => Math.Abs(left - right) <= Math.Max(1e-12, Math.Abs(right) * 1e-9);

    private static double ToMiB(long bytes) => bytes / (1024d * 1024d);

    private static string RequiredEnvironmentPath(string canonicalName, string legacyName)
    {
        string? value = Environment.GetEnvironmentVariable(canonicalName);
        if (string.IsNullOrWhiteSpace(value))
            value = Environment.GetEnvironmentVariable(legacyName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Set process environment variable {canonicalName}.");

        string fullPath = Path.GetFullPath(value);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            throw new FileNotFoundException($"Configured input was not found for {canonicalName}.", fullPath);
        return fullPath;
    }

    private static void VerifyFile(string path, long expectedLength, string expectedSha256, string description)
    {
        var info = new FileInfo(path);
        if (info.Length != expectedLength)
            throw new InvalidDataException($"Unexpected {description} size: {info.Length} bytes; expected {expectedLength}.");
        using FileStream input = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected {description} SHA-256: {actual}.");
    }

    private static float[] LoadPcmFixture(string path)
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("The verified PCM fixture is little-endian float32.");

        VerifyFile(path, ExpectedPcmBytes, ExpectedPcmSha256, "PCM fixture");
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length % sizeof(float) != 0)
            throw new InvalidDataException("PCM fixture size is not divisible by float32 size.");
        var samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        foreach (float sample in samples)
        {
            if (!float.IsFinite(sample))
                throw new InvalidDataException("PCM fixture contains a non-finite sample.");
        }
        return samples;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --project tests/NemotronDiarizationSharp.LongRun -c Release -- " +
                          "--duration-minutes <n> [--chunk-seconds <n>] [--poll-seconds <n>] " +
                          "[--diagnostic-interval-seconds <n>] [--max-runtime-minutes <n>] [--require-compaction]");
        Console.WriteLine("Required process environment: NEMOTONDIARIZATIONSHARP_MODEL, " +
                          "NEMOTONDIARIZATIONSHARP_PCM_F32LE, NEMOTONDIARIZATIONSHARP_NATIVE_DIR.");
    }

    private sealed record SegmentSummary(int Count, string Sha256);

    private sealed class RunSampler
    {
        private readonly double _intervalSeconds;
        private double _nextAtLogicalSeconds;

        internal RunSampler(double intervalSeconds)
        {
            _intervalSeconds = intervalSeconds;
            _nextAtLogicalSeconds = intervalSeconds;
        }

        internal bool IsDue(double logicalSeconds)
        {
            if (logicalSeconds < _nextAtLogicalSeconds)
                return false;
            _nextAtLogicalSeconds = logicalSeconds + _intervalSeconds;
            return true;
        }
    }

    private sealed class RunOptions
    {
        internal double DurationMinutes { get; private init; } = 0.25;
        internal double ChunkSeconds { get; private init; } = 1;
        internal double PollSeconds { get; private init; } = 5;
        internal double DiagnosticIntervalSeconds { get; private init; } = 60;
        internal double MaxRuntimeMinutes { get; private init; } = 120;
        internal bool RequireCompaction { get; private init; }
        internal bool ShowHelp { get; private init; }

        internal static RunOptions Parse(string[] args)
        {
            double duration = 0.25;
            double chunk = 1;
            double poll = 5;
            double diagnosticInterval = 60;
            double maxRuntime = 120;
            bool showHelp = false;
            bool requireCompaction = false;

            for (int index = 0; index < args.Length; index++)
            {
                string option = args[index];
                if (option is "--help" or "-h")
                {
                    showHelp = true;
                    continue;
                }
                if (option == "--require-compaction")
                {
                    requireCompaction = true;
                    continue;
                }
                if (index + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {option}.");

                string value = args[++index];
                double parsed = ParsePositive(value, option);
                switch (option)
                {
                    case "--duration-minutes": duration = parsed; break;
                    case "--chunk-seconds": chunk = parsed; break;
                    case "--poll-seconds": poll = parsed; break;
                    case "--diagnostic-interval-seconds": diagnosticInterval = parsed; break;
                    case "--max-runtime-minutes": maxRuntime = parsed; break;
                    default: throw new ArgumentException($"Unknown option '{option}'.");
                }
            }

            if (chunk > 30)
                throw new ArgumentOutOfRangeException(nameof(args), "Chunk duration must not exceed 30 seconds.");
            // Target duration is logical audio time, while max runtime is elapsed wall-clock time.
            return new RunOptions
            {
                DurationMinutes = duration,
                ChunkSeconds = chunk,
                PollSeconds = poll,
                DiagnosticIntervalSeconds = diagnosticInterval,
                MaxRuntimeMinutes = maxRuntime,
                RequireCompaction = requireCompaction,
                ShowHelp = showHelp
            };
        }

        private static double ParsePositive(string value, string option)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
                !double.IsFinite(parsed) || parsed <= 0)
                throw new ArgumentException($"{option} must be a finite positive number.");
            return parsed;
        }
    }
}
