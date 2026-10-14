using NemotronDiarizationSharp;
using NemotronDiarizationSharp.Native;
using System.Runtime.CompilerServices;
using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class WindowsNativeInferenceTests
{
    private readonly ITestOutputHelper _output;

    public WindowsNativeInferenceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void CpuRuntimeRunsOfflineInferenceWithCompletedManagedResult()
    {
        RequireWindowsX64();

        string? modelPath = GetConfiguredPath("NEMOTONDIARIZATIONSHARP_MODEL", "NEMOTONSHARP_MODEL");
        string? pcmPath = GetConfiguredPath("NEMOTONDIARIZATIONSHARP_PCM_F32LE", "NEMOTONSHARP_PCM_F32LE");
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(pcmPath))
            Assert.Skip("Set NEMOTONDIARIZATIONSHARP_MODEL and NEMOTONDIARIZATIONSHARP_PCM_F32LE to run native CPU inference.");

        float[] samples = ReadPcm(pcmPath);
        using var model = DiarizationModel.Load(modelPath);
        Assert.StartsWith("nemo-speech-asr 0.2.0", DiarizationModel.NativeRuntimeVersion, StringComparison.Ordinal);

        DiarizationSnapshot emptyResult = model.ProcessOffline(ReadOnlySpan<float>.Empty, sampleRate: 16000);
        Assert.True(emptyResult.IsFinal);
        Assert.Equal(1, emptyResult.FrameCount);
        Assert.Equal(0, emptyResult.FirstProbabilityFrame);
        Assert.Empty(emptyResult.Segments);
        for (int speaker = 0; speaker < emptyResult.SpeakerCount; speaker++)
            Assert.True(float.IsFinite(emptyResult.GetProbability(0, speaker)));

        DiarizationSnapshot result = model.ProcessOffline(samples, sampleRate: 16000);
        DiarizationSnapshot filtered = model.ProcessOffline(samples, sampleRate: 16000,
            new DiarizationSegmentationOptions { MinDurationSeconds = 30 });

        Assert.True(result.IsFinal);
        Assert.Equal(0, result.FirstProbabilityFrame);
        Assert.True(result.FrameCount > 0);
        Assert.Equal(model.SpeakerCount, result.SpeakerCount);
        Assert.Equal(model.SecondsPerFrame, result.SecondsPerFrame);
        Assert.Equal(0d, result.GetFrameTimestamp(0));
        if (result.FrameCount > 1)
            Assert.Equal(result.SecondsPerFrame, result.GetFrameTimestamp(1), precision: 8);

        long retainedFrames = result.FrameCount - result.FirstProbabilityFrame;
        Assert.Equal(result.FrameCount, retainedFrames);
        for (long frame = result.FirstProbabilityFrame; frame < result.FrameCount; frame++)
        {
            for (int speaker = 0; speaker < result.SpeakerCount; speaker++)
            {
                float probability = result.GetProbability(frame, speaker);
                Assert.True(float.IsFinite(probability));
                Assert.InRange(probability, 0f, 1f);
            }
        }

        double previousStart = -1;
        foreach (DiarizationSegment segment in result.Segments)
        {
            Assert.True(double.IsFinite(segment.StartTime));
            Assert.True(double.IsFinite(segment.EndTime));
            Assert.True(segment.StartTime >= previousStart);
            Assert.True(segment.StartTime >= 0 && segment.EndTime >= segment.StartTime);
            Assert.InRange(segment.Speaker, 1, result.SpeakerCount);
            previousStart = segment.StartTime;
        }
        Assert.NotEmpty(result.Segments);
        Assert.Empty(filtered.Segments);

        _output.WriteLine($"Offline runtime {DiarizationModel.NativeRuntimeVersion}; speakers {result.SpeakerCount}; frames {result.FrameCount}; cadence {result.SecondsPerFrame:F3}s; segments: {string.Join(", ", result.Segments.Select(segment => $"speaker {segment.Speaker} {segment.StartTime:F2}-{segment.EndTime:F2}s"))}");

        model.Dispose();
        Assert.Throws<ObjectDisposedException>(() => model.ProcessOffline(samples, sampleRate: 16000));
        Assert.True(result.IsFinal);
        Assert.Equal(retainedFrames, result.FrameCount);
        _ = result.GetProbability(0, 0);
    }

    [Fact]
    public void CpuRuntimeRunsInferenceWhenModelAndPcmPathsAreConfigured()
    {
        RequireWindowsX64();

        string? modelPath = GetConfiguredPath("NEMOTONDIARIZATIONSHARP_MODEL", "NEMOTONSHARP_MODEL");
        string? pcmPath = GetConfiguredPath("NEMOTONDIARIZATIONSHARP_PCM_F32LE", "NEMOTONSHARP_PCM_F32LE");
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(pcmPath))
            Assert.Skip("Set NEMOTONDIARIZATIONSHARP_MODEL and NEMOTONDIARIZATIONSHARP_PCM_F32LE to run native CPU inference.");

        float[] samples = ReadPcm(pcmPath);

        using var model = DiarizationModel.Load(modelPath);
        Assert.StartsWith("nemo-speech-asr 0.2.0", DiarizationModel.NativeRuntimeVersion, StringComparison.Ordinal);
        Assert.True(model.SpeakerCount > 0);
        Assert.True(model.SecondsPerFrame > 0);

        using var stream = model.OpenStream();
        stream.Push(ReadOnlySpan<float>.Empty, sampleRate: 16000);
        int split = samples.Length / 2;
        stream.Push(samples.AsSpan(0, split), sampleRate: 16000);
        stream.Push(samples.AsSpan(split), sampleRate: 16000);
        Assert.Throws<ArgumentException>(() => stream.Push([0f], sampleRate: 48000));
        Assert.Throws<ArgumentException>(() => stream.Push([float.NaN], sampleRate: 16000));
        stream.Finish();
        stream.Finish();
        DiarizationSnapshot result = stream.GetSnapshot();
        DiarizationSnapshot explicitDefaults = stream.GetSnapshot(new DiarizationSegmentationOptions());
        DiarizationSnapshot filtered = stream.GetSnapshot(new DiarizationSegmentationOptions { MinDurationSeconds = 30 });

        Assert.True(result.IsFinal);
        Assert.True(result.FrameCount > 0);
        Assert.Equal(model.SpeakerCount, result.SpeakerCount);
        for (long frame = result.FirstProbabilityFrame; frame < result.FrameCount; frame++)
        {
            Assert.True(double.IsFinite(result.GetFrameTimestamp(frame)));
            for (int speaker = 0; speaker < result.SpeakerCount; speaker++)
                Assert.InRange(result.GetProbability(frame, speaker), 0f, 1f);
        }

        double previousStart = -1;
        foreach (DiarizationSegment segment in result.Segments)
        {
            Assert.True(double.IsFinite(segment.StartTime));
            Assert.True(double.IsFinite(segment.EndTime));
            Assert.True(segment.StartTime >= previousStart);
            Assert.True(segment.StartTime >= 0 && segment.EndTime >= segment.StartTime);
            Assert.InRange(segment.Speaker, 1, result.SpeakerCount);
            previousStart = segment.StartTime;
        }
        Assert.Equal(result.Segments, explicitDefaults.Segments);
        Assert.NotEmpty(result.Segments);
        Assert.Empty(filtered.Segments);

        using (var emptyStream = model.OpenStream())
        {
            emptyStream.Push(ReadOnlySpan<float>.Empty, sampleRate: 16000);
            emptyStream.Finish();
            DiarizationSnapshot empty = emptyStream.GetSnapshot();
            Assert.True(empty.IsFinal);
            Assert.Equal(0, empty.FrameCount);
        }

        model.Dispose();
        model.Dispose();
        Assert.Throws<ObjectDisposedException>(() => model.OpenStream());
        Assert.Equal(result.FrameCount, stream.GetSnapshot().FrameCount);
        stream.Dispose();
        stream.Dispose();
        Assert.Throws<ObjectDisposedException>(() => stream.GetSnapshot());
        Assert.Throws<ObjectDisposedException>(() => stream.Push([0f], sampleRate: 16000));
        Assert.Throws<ObjectDisposedException>(() => stream.Finish());
    }

    [Fact]
    public void CpuRuntimeReadsIncrementalFramesThroughFinishWithoutDuplicates()
    {
        RequireWindowsX64();

        string? modelPath = GetConfiguredPath("NEMOTONDIARIZATIONSHARP_MODEL", "NEMOTONSHARP_MODEL");
        string? pcmPath = GetConfiguredPath("NEMOTONDIARIZATIONSHARP_PCM_F32LE", "NEMOTONSHARP_PCM_F32LE");
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(pcmPath))
            Assert.Skip("Set NEMOTONDIARIZATIONSHARP_MODEL and NEMOTONDIARIZATIONSHARP_PCM_F32LE to run native CPU inference.");

        float[] samples = ReadPcm(pcmPath);
        using var model = DiarizationModel.Load(modelPath);
        using var stream = model.OpenStream();
        var batches = new List<DiarizationFrameBatch>();
        long nextFrame = 0;
        long changedPublishedProbabilities = 0;
        int chunkSize = samples.Length / 4;

        for (int start = 0; start < samples.Length; start += chunkSize)
        {
            int count = Math.Min(chunkSize, samples.Length - start);
            stream.Push(samples.AsSpan(start, count), sampleRate: 16000);
            DiarizationSnapshot beforeRead = stream.GetSnapshot();
            Assert.Equal(0, beforeRead.FirstProbabilityFrame);

            foreach (DiarizationFrameBatch previous in batches)
            {
                long end = previous.FirstFrameIndex + previous.FrameCount;
                long checkStart = Math.Max(previous.FirstFrameIndex, beforeRead.FirstProbabilityFrame);
                long checkEnd = Math.Min(end, beforeRead.FrameCount);
                for (long frame = checkStart; frame < checkEnd; frame++)
                {
                    for (int speaker = 0; speaker < previous.SpeakerCount; speaker++)
                    {
                        if (previous.GetProbability(frame, speaker) != beforeRead.GetProbability(frame, speaker))
                            changedPublishedProbabilities++;
                    }
                }
            }

            DiarizationFrameBatch batch = stream.ReadNewFrames();
            Assert.Equal(nextFrame, batch.FirstFrameIndex);
            Assert.Equal(beforeRead.FrameCount - nextFrame, batch.FrameCount);
            Assert.Equal(beforeRead.SpeakerCount, batch.SpeakerCount);
            nextFrame = checked(nextFrame + batch.FrameCount);
            batches.Add(batch);

            DiarizationFrameBatch empty = stream.ReadNewFrames();
            Assert.Equal(nextFrame, empty.FirstFrameIndex);
            Assert.Equal(0, empty.FrameCount);
        }

        stream.Finish();
        DiarizationFrameBatch finalTail = stream.ReadNewFrames();
        Assert.Equal(nextFrame, finalTail.FirstFrameIndex);
        Assert.True(finalTail.FrameCount > 0, "Finish should publish the remaining frame tail for this fixture.");
        nextFrame = checked(nextFrame + finalTail.FrameCount);
        batches.Add(finalTail);
        Assert.Equal(0, stream.ReadNewFrames().FrameCount);

        DiarizationSnapshot finalSnapshot = stream.GetSnapshot();
        Assert.Equal(finalSnapshot.FrameCount, nextFrame);
        Assert.Equal(0, finalSnapshot.FirstProbabilityFrame);
        Assert.Equal(model.SpeakerCount, finalSnapshot.SpeakerCount);

        long mismatchesAgainstFinal = 0;
        foreach (DiarizationFrameBatch batch in batches)
        {
            for (int offset = 0; offset < batch.FrameCount; offset++)
            {
                long frame = batch.FirstFrameIndex + offset;
                for (int speaker = 0; speaker < batch.SpeakerCount; speaker++)
                {
                    if (batch.GetProbability(frame, speaker) != finalSnapshot.GetProbability(frame, speaker))
                        mismatchesAgainstFinal++;
                }
            }
        }

        _output.WriteLine($"Incremental batches returned {nextFrame} frames; published probabilities changed during later pushes: {changedPublishedProbabilities}; differences from final snapshot: {mismatchesAgainstFinal}.");
        Assert.StartsWith("nemo-speech-asr 0.2.0", DiarizationModel.NativeRuntimeVersion, StringComparison.Ordinal);

        stream.Dispose();
        Assert.Throws<ObjectDisposedException>(() => stream.ReadNewFrames());
        DiarizationFrameBatch retained = batches.First(batch => batch.FrameCount > 0);
        _ = retained.GetProbability(retained.FirstFrameIndex, 0);
    }

    [Fact]
    public async Task CpuRuntimeAsyncPushMatchesSynchronousChunksAndKeepsModelAlive()
    {
        RequireWindowsX64();

        string? modelPath = GetConfiguredPath("NEMOTONDIARIZATIONSHARP_MODEL", "NEMOTONSHARP_MODEL");
        string? pcmPath = GetConfiguredPath("NEMOTONDIARIZATIONSHARP_PCM_F32LE", "NEMOTONSHARP_PCM_F32LE");
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(pcmPath))
            Assert.Skip("Set NEMOTONDIARIZATIONSHARP_MODEL and NEMOTONDIARIZATIONSHARP_PCM_F32LE to run native CPU inference.");

        float[] samples = ReadPcm(pcmPath);
        using var model = DiarizationModel.Load(modelPath);
        using var synchronous = model.OpenStream();
        using var asynchronous = model.OpenStream();
        model.Dispose();

        int chunkSize = samples.Length / 4;
        var chunks = new List<ReadOnlyMemory<float>>();
        for (int start = 0; start < samples.Length; start += chunkSize)
        {
            int count = Math.Min(chunkSize, samples.Length - start);
            chunks.Add(samples.AsMemory(start, count));
            synchronous.Push(samples.AsSpan(start, count), sampleRate: 16000);
        }

        await asynchronous.PushChunksAsync(YieldChunksAsync(chunks, TestContext.Current.CancellationToken), sampleRate: 16000,
            TestContext.Current.CancellationToken);
        synchronous.Finish();
        asynchronous.Finish();
        DiarizationSnapshot syncResult = synchronous.GetSnapshot();
        DiarizationSnapshot asyncResult = asynchronous.GetSnapshot();

        Assert.Equal(syncResult.FrameCount, asyncResult.FrameCount);
        Assert.Equal(syncResult.FirstProbabilityFrame, asyncResult.FirstProbabilityFrame);
        Assert.Equal(syncResult.SpeakerCount, asyncResult.SpeakerCount);
        Assert.Equal(syncResult.SecondsPerFrame, asyncResult.SecondsPerFrame);
        Assert.Equal(syncResult.Segments, asyncResult.Segments);

        float maxDifference = 0;
        for (long frame = syncResult.FirstProbabilityFrame; frame < syncResult.FrameCount; frame++)
        {
            for (int speaker = 0; speaker < syncResult.SpeakerCount; speaker++)
            {
                maxDifference = Math.Max(maxDifference,
                    Math.Abs(syncResult.GetProbability(frame, speaker) - asyncResult.GetProbability(frame, speaker)));
            }
        }

        _output.WriteLine($"Synchronous/async native comparison: runtime {DiarizationModel.NativeRuntimeVersion}; frames {asyncResult.FrameCount}; max probability difference {maxDifference:G9}.");
        Assert.InRange(maxDifference, 0f, 1e-5f);
    }

    [Fact]
    public async Task CpuRuntimeRejectsConflictingOperationsAndResumesAfterCancellation()
    {
        RequireWindowsX64();

        string? modelPath = GetConfiguredPath("NEMOTONDIARIZATIONSHARP_MODEL", "NEMOTONSHARP_MODEL");
        string? pcmPath = GetConfiguredPath("NEMOTONDIARIZATIONSHARP_PCM_F32LE", "NEMOTONSHARP_PCM_F32LE");
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(pcmPath))
            Assert.Skip("Set NEMOTONDIARIZATIONSHARP_MODEL and NEMOTONDIARIZATIONSHARP_PCM_F32LE to run native CPU inference.");

        float[] samples = ReadPcm(pcmPath);
        using var model = DiarizationModel.Load(modelPath);
        using var stream = model.OpenStream();
        int split = samples.Length / 2;
        using var preCanceled = new CancellationTokenSource();
        preCanceled.Cancel();
        using var linkedPreCanceled = CancellationTokenSource.CreateLinkedTokenSource(
            preCanceled.Token, TestContext.Current.CancellationToken);
        Action canceledStart = () => _ = stream.PushChunksAsync(
            YieldChunksAsync([], linkedPreCanceled.Token), sampleRate: 16000, linkedPreCanceled.Token);
        Assert.Throws<OperationCanceledException>(canceledStart);

        stream.Push(samples.AsSpan(0, 1), sampleRate: 16000);
        Action invalidRate = () => _ = stream.PushChunksAsync(
            YieldChunksAsync([], TestContext.Current.CancellationToken), sampleRate: 7999,
            TestContext.Current.CancellationToken);
        Action changedRate = () => _ = stream.PushChunksAsync(
            YieldChunksAsync([], TestContext.Current.CancellationToken), sampleRate: 48000,
            TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentOutOfRangeException>(invalidRate);
        Assert.Throws<ArgumentException>(changedRate);
        using var cancellation = new CancellationTokenSource();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellation.Token, TestContext.Current.CancellationToken);
        var waitingForNextChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task operation = stream.PushChunksAsync(
            YieldThenWaitAsync(samples.AsMemory(1, split - 1), waitingForNextChunk, linkedCancellation.Token),
            sampleRate: 16000, linkedCancellation.Token);
        await waitingForNextChunk.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() => stream.Push([0f], sampleRate: 16000));
        Assert.Throws<InvalidOperationException>(() => stream.Finish());
        Assert.Throws<InvalidOperationException>(() => stream.GetSnapshot());
        Assert.Throws<InvalidOperationException>(() => stream.ReadNewFrames());
        Assert.Throws<InvalidOperationException>(() => stream.Dispose());
        Action overlappingAsyncPush = () => _ = stream.PushChunksAsync(
            YieldChunksAsync([], TestContext.Current.CancellationToken), sampleRate: 16000,
            TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(overlappingAsyncPush);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        ReadOnlyMemory<float>[] invalidChunks = [new float[] { float.NaN }];
        await Assert.ThrowsAsync<ArgumentException>(() =>
            stream.PushChunksAsync(YieldChunksAsync(invalidChunks, TestContext.Current.CancellationToken), sampleRate: 16000,
                TestContext.Current.CancellationToken));

        // Cancellation preserves the successfully pushed prefix; the caller can supply its remainder.
        stream.Push(samples.AsSpan(split), sampleRate: 16000);
        stream.Finish();
        DiarizationSnapshot result = stream.GetSnapshot();
        Assert.True(result.IsFinal);
        Assert.Equal(801, result.FrameCount);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<float>> YieldChunksAsync(
        IEnumerable<ReadOnlyMemory<float>> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (ReadOnlyMemory<float> chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<float>> YieldThenWaitAsync(
        ReadOnlyMemory<float> firstChunk,
        TaskCompletionSource waiting,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return firstChunk;
        waiting.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield return ReadOnlyMemory<float>.Empty;
    }

    [Fact]
    public void CpuRuntimeAcceptsV3PresetAndPropagatesNativeConfigurationErrors()
    {
        RequireWindowsX64();

        string? modelPath = GetConfiguredPath("NEMOTONDIARIZATIONSHARP_MODEL", "NEMOTONSHARP_MODEL");
        if (string.IsNullOrWhiteSpace(modelPath))
            Assert.Skip("Set NEMOTONDIARIZATIONSHARP_MODEL to run native CPU inference.");

        using (DiarizationModel model = DiarizationModel.Load(modelPath, new DiarizationModelOptions
        {
            Preset = "v3-streaming",
            LeftContextFrames = 0
        }))
        {
            Assert.Equal(8, model.SpeakerCount);
            Assert.Equal(0.01, model.SecondsPerFrame, precision: 8);
            using DiarizationStream stream = model.OpenStream();
            stream.Finish();
            Assert.True(stream.GetSnapshot().IsFinal);
        }

        NativeCallException unsupportedPreset = Assert.Throws<NativeCallException>(() =>
            DiarizationModel.Load(modelPath, new DiarizationModelOptions { Preset = "unsupported-preset" }));
        Assert.Contains("InvalidArgument", unsupportedPreset.Message, StringComparison.Ordinal);

        NativeCallException invalidGeometry = Assert.Throws<NativeCallException>(() =>
            DiarizationModel.Load(modelPath, new DiarizationModelOptions
            {
                Preset = "v3-streaming",
                SpeakerCacheFrames = 1,
                UpdatePeriodFrames = 2
            }));
        Assert.Contains("InvalidArgument", invalidGeometry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeCreationFailureIncludesThreadLocalError()
    {
        RequireWindowsX64();

        var config = new DiarModelConfig
        {
            Size = (nuint)System.Runtime.InteropServices.Marshal.SizeOf<DiarModelConfig>(),
            Gpu = -1,
            LeftContextFrames = -1
        };
        NativeStatus status = NativeMethods.DiarCreate(in config, out nint model);
        NativeCallException? exception = status == NativeStatus.Ok ? null : NativeCallException.Create(status);
        if (model != 0)
            NativeMethods.DiarDestroy(model);

        Assert.NotEqual(NativeStatus.Ok, status);
        NativeCallException nativeException = exception
            ?? throw new InvalidOperationException("The native failure did not produce an exception.");
        Assert.Contains($"({status})", nativeException.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(nativeException.Message));
    }

    private static string? GetConfiguredPath(string canonicalName, string legacyName)
    {
        string? value = Environment.GetEnvironmentVariable(canonicalName);
        return string.IsNullOrWhiteSpace(value) ? Environment.GetEnvironmentVariable(legacyName) : value;
    }

    private static float[] ReadPcm(string pcmPath)
    {
        byte[] bytes = File.ReadAllBytes(pcmPath);
        Assert.NotEmpty(bytes);
        Assert.Equal(0, bytes.Length % sizeof(float));
        float[] samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    private static void RequireWindowsX64()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            Assert.Skip("The native integration tests require a Windows x64 process.");
    }
}
