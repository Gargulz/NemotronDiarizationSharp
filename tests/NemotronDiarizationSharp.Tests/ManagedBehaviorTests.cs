using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using NemotronDiarizationSharp;
using NemotronDiarizationSharp.Native;
using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class ManagedBehaviorTests
{
    [Fact]
    public void PartialStreamCloseFailureRetainsModelReference()
    {
        bool released = false;

        Assert.Throws<InvalidOperationException>(() => DiarizationModel.ClosePartialStreamAndReleaseModel(
            (nint)1,
            _ => throw new InvalidOperationException("close failed"),
            () => released = true));

        Assert.False(released);
    }

    [Fact]
    public void PartialStreamCloseSuccessReleasesModelReference()
    {
        bool closed = false;
        bool released = false;

        DiarizationModel.ClosePartialStreamAndReleaseModel(
            (nint)1,
            _ => closed = true,
            () => released = true);

        Assert.True(closed);
        Assert.True(released);
    }

    [Fact]
    public void MissingPartialStreamReleasesModelReferenceWithoutClosing()
    {
        bool released = false;

        DiarizationModel.ClosePartialStreamAndReleaseModel(
            0,
            _ => throw new InvalidOperationException("close should not run"),
            () => released = true);

        Assert.True(released);
    }

    [Fact]
    public void SnapshotMapsRetainedProbabilityFramesAndTimestamps()
    {
        var snapshot = new DiarizationSnapshot(
            firstProbabilityFrame: 4,
            frameCount: 6,
            speakerCount: 2,
            secondsPerFrame: 0.01,
            probabilities: [0.1f, 0.2f, 0.3f, 0.4f],
            segments: [],
            isFinal: true);

        Assert.Equal(0.3f, snapshot.GetProbability(frame: 5, speakerIndex: 0));
        Assert.Equal(0.4f, snapshot.GetProbability(frame: 5, speakerIndex: 1));
        Assert.Equal(0.05, snapshot.GetFrameTimestamp(frame: 5), precision: 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetProbability(frame: 3, speakerIndex: 0));
    }

    [Fact]
    public void SnapshotRetainsOverlappingSpeakerSegments()
    {
        var first = new DiarizationSegment(1.0, 2.0, 1);
        var second = new DiarizationSegment(1.5, 2.5, 2);
        var snapshot = new DiarizationSnapshot(0, 10, 2, 0.1, new float[20], [first, second], isFinal: true);

        Assert.Equal(2, snapshot.Segments.Count);
        Assert.True(snapshot.Segments[0].StartTime < snapshot.Segments[1].EndTime
            && snapshot.Segments[1].StartTime < snapshot.Segments[0].EndTime);
        Assert.Equal(2, snapshot.Segments[1].Speaker);
    }

    [Fact]
    public void SnapshotRejectsProbabilityBufferThatDoesNotMatchRetainedFrames()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new DiarizationSnapshot(4, 6, 2, 0.01, [0.1f, 0.2f], [], isFinal: false));
    }

    [Fact]
    public void IncrementalCursorReturnsOnlyNewFrameMajorValuesWithAbsoluteIndexes()
    {
        var cursor = new IncrementalFrameCursor();

        DiarizationFrameBatch first = cursor.CreateBatch(0, 2, 2, 0.01, [0.1f, 0.2f, 0.3f, 0.4f]);
        Assert.Equal(0, first.FirstFrameIndex);
        Assert.Equal(2, first.FrameCount);
        Assert.Equal(2, first.SpeakerCount);
        Assert.Equal(0.01, first.SecondsPerFrame);
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f, 0.4f }, first.Probabilities);
        Assert.Equal(0.4f, first.GetProbability(frameIndex: 1, speakerIndex: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.GetProbability(frameIndex: 2, speakerIndex: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.GetProbability(frameIndex: 0, speakerIndex: 2));

        DiarizationFrameBatch second = cursor.CreateBatch(0, 4, 2, 0.01,
            [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f]);
        Assert.Equal(2, second.FirstFrameIndex);
        Assert.Equal(2, second.FrameCount);
        Assert.Equal(new[] { 0.5f, 0.6f, 0.7f, 0.8f }, second.Probabilities);
        Assert.Equal(0.8f, second.GetProbability(frameIndex: 3, speakerIndex: 1));

        DiarizationFrameBatch empty = cursor.CreateBatch(0, 4, 2, 0.01,
            [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f]);
        Assert.Equal(4, empty.FirstFrameIndex);
        Assert.Equal(0, empty.FrameCount);
        Assert.Empty(empty.Probabilities);
        Assert.Equal(4, cursor.NextUnreadFrame);
    }

    [Fact]
    public void IncrementalCursorEmptyReadDoesNotAdvanceAndCursorsAreIndependent()
    {
        var firstCursor = new IncrementalFrameCursor();
        var secondCursor = new IncrementalFrameCursor();

        DiarizationFrameBatch empty = firstCursor.CreateBatch(0, 0, 1, 0.08, []);
        Assert.Equal(0, empty.FirstFrameIndex);
        Assert.Equal(0, empty.FrameCount);
        Assert.Equal(0, firstCursor.NextUnreadFrame);

        _ = firstCursor.CreateBatch(0, 1, 1, 0.08, [0.25f]);
        Assert.Equal(1, firstCursor.NextUnreadFrame);
        Assert.Equal(0, secondCursor.NextUnreadFrame);
        DiarizationFrameBatch independent = secondCursor.CreateBatch(0, 1, 1, 0.08, [0.75f]);
        Assert.Equal(0, independent.FirstFrameIndex);
        Assert.Equal(0.75f, independent.GetProbability(0, 0));
    }

    [Fact]
    public void IncrementalCursorDetectsHistoryGapWithoutAdvancing()
    {
        var cursor = new IncrementalFrameCursor();
        _ = cursor.CreateBatch(0, 2, 1, 0.08, [0.1f, 0.2f]);

        DiarizationFrameHistoryGapException exception = Assert.Throws<DiarizationFrameHistoryGapException>(
            () => cursor.CreateBatch(4, 6, 1, 0.08, [0.4f, 0.5f]));
        Assert.Equal(2, exception.MissingStartFrame);
        Assert.Equal(4, exception.MissingEndFrameExclusive);
        Assert.Equal(4, exception.EarliestAvailableFrame);
        Assert.Contains("[2, 4)", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, cursor.NextUnreadFrame);

        DiarizationFrameBatch retry = cursor.CreateBatch(2, 4, 1, 0.08, [0.3f, 0.4f]);
        Assert.Equal(2, retry.FirstFrameIndex);
        Assert.Equal(2, retry.FrameCount);
        Assert.Equal(4, cursor.NextUnreadFrame);
    }

    [Fact]
    public void IncrementalCursorRejectsInvalidNativeMetadataAndArithmeticBounds()
    {
        var cursor = new IncrementalFrameCursor();

        Assert.Throws<InvalidOperationException>(() => cursor.CreateBatch(-1, 0, 1, 0.08, []));
        Assert.Throws<InvalidOperationException>(() => cursor.CreateBatch(2, 1, 1, 0.08, []));
        Assert.Throws<InvalidOperationException>(() => cursor.CreateBatch(0, 1, 0, 0.08, []));
        Assert.Throws<InvalidOperationException>(() => cursor.CreateBatch(0, 1, 1, double.NaN, [0.5f]));
        Assert.Throws<InvalidOperationException>(() => cursor.CreateBatch(0, 1, 1, 0.08, []));
        Assert.Throws<InvalidOperationException>(() => cursor.CreateBatch(0, 1, 1, 0.08, [float.NaN]));
        Assert.Throws<InvalidOperationException>(() => cursor.CreateBatch(0, long.MaxValue, 1, 0.08, []));
    }

    [Fact]
    public async Task AsyncChunkProcessorHandlesEmptySourceAndPreservesOrder()
    {
        var processed = new List<float>();
        await DiarizationStream.ProcessChunksSequentiallyAsync(
            YieldChunksAsync([], TestContext.Current.CancellationToken), TestContext.Current.CancellationToken,
            chunk =>
            {
                processed.AddRange(chunk.ToArray());
                return Task.CompletedTask;
            });
        Assert.Empty(processed);

        await DiarizationStream.ProcessChunksSequentiallyAsync(
            YieldChunksAsync([[1f, 2f], [3f], [4f, 5f]], TestContext.Current.CancellationToken), TestContext.Current.CancellationToken,
            chunk =>
            {
                processed.AddRange(chunk.ToArray());
                return Task.CompletedTask;
            });

        Assert.Equal(new[] { 1f, 2f, 3f, 4f, 5f }, processed);

        await DiarizationStream.ProcessChunksSequentiallyAsync(
            YieldChunksAsync([[6f]], TestContext.Current.CancellationToken), TestContext.Current.CancellationToken,
            chunk =>
            {
                processed.AddRange(chunk.ToArray());
                return Task.CompletedTask;
            });
        Assert.Equal(new[] { 1f, 2f, 3f, 4f, 5f, 6f }, processed);
    }

    [Fact]
    public async Task AsyncChunkProcessorDoesNotRequestNextChunkUntilCurrentProcessingCompletes()
    {
        var firstProcessing = NewSignal();
        var allowFirstToComplete = NewSignal();
        var secondRequested = NewSignal();
        int processed = 0;

        async IAsyncEnumerable<ReadOnlyMemory<float>> Source(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new float[] { 1f };
            secondRequested.TrySetResult();
            yield return new float[] { 2f };
            await Task.CompletedTask;
        }

        Task operation = DiarizationStream.ProcessChunksSequentiallyAsync(
            Source(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken,
            chunk =>
            {
                processed++;
                if (processed == 1)
                {
                    firstProcessing.TrySetResult();
                    return allowFirstToComplete.Task;
                }
                return Task.CompletedTask;
            });

        await firstProcessing.Task;
        Assert.False(secondRequested.Task.IsCompleted);
        Assert.Equal(1, processed);
        allowFirstToComplete.TrySetResult();
        await operation;
        Assert.True(secondRequested.Task.IsCompleted);
        Assert.Equal(2, processed);
    }

    [Fact]
    public async Task AsyncChunkProcessorObservesCancellationWhileWaitingAndBetweenChunks()
    {
        using (var alreadyCanceled = new CancellationTokenSource())
        {
            alreadyCanceled.Cancel();
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                alreadyCanceled.Token, TestContext.Current.CancellationToken);
            int processedBeforeStart = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DiarizationStream.ProcessChunksSequentiallyAsync(
                    YieldChunksAsync([[0f]], linkedCancellation.Token), linkedCancellation.Token,
                    _ =>
                    {
                        processedBeforeStart++;
                        return Task.CompletedTask;
                    }));
            Assert.Equal(0, processedBeforeStart);
        }

        var waiting = NewSignal();
        using (var waitingCancellation = new CancellationTokenSource())
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                waitingCancellation.Token, TestContext.Current.CancellationToken);
            int processed = 0;
            Task waitingOperation = DiarizationStream.ProcessChunksSequentiallyAsync(
                WaitForCancellationAsync(waiting, linkedCancellation.Token), linkedCancellation.Token,
                _ =>
                {
                    processed++;
                    return Task.CompletedTask;
                });

            await waiting.Task;
            waitingCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingOperation);
            Assert.Equal(0, processed);
        }

        using var betweenChunksCancellation = new CancellationTokenSource();
        using var linkedBetweenChunksCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            betweenChunksCancellation.Token, TestContext.Current.CancellationToken);
        var firstProcessing = NewSignal();
        var allowFirstToComplete = NewSignal();
        var processedValues = new List<float>();
        Task betweenChunksOperation = DiarizationStream.ProcessChunksSequentiallyAsync(
            YieldChunksAsync([[1f], [2f]], linkedBetweenChunksCancellation.Token), linkedBetweenChunksCancellation.Token,
            chunk =>
            {
                processedValues.Add(chunk.Span[0]);
                if (processedValues.Count == 1)
                {
                    firstProcessing.TrySetResult();
                    return allowFirstToComplete.Task;
                }
                return Task.CompletedTask;
            });

        await firstProcessing.Task;
        betweenChunksCancellation.Cancel();
        allowFirstToComplete.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => betweenChunksOperation);
        Assert.Equal(new[] { 1f }, processedValues);
    }

    [Fact]
    public async Task AsyncChunkProcessorPropagatesSourceAndProcessingExceptions()
    {
        var sourceError = new InvalidOperationException("source failed");
        async IAsyncEnumerable<ReadOnlyMemory<float>> FaultingSource()
        {
            yield return new float[] { 1f };
            await Task.Yield();
            throw sourceError;
        }

        InvalidOperationException observedSourceError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DiarizationStream.ProcessChunksSequentiallyAsync(FaultingSource(), TestContext.Current.CancellationToken,
                _ => Task.CompletedTask));
        Assert.Same(sourceError, observedSourceError);

        var processingError = new InvalidOperationException("native push failed");
        InvalidOperationException observedProcessingError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DiarizationStream.ProcessChunksSequentiallyAsync(
                YieldChunksAsync([[1f]], TestContext.Current.CancellationToken), TestContext.Current.CancellationToken,
                _ => Task.FromException(processingError)));
        Assert.Same(processingError, observedProcessingError);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async IAsyncEnumerable<ReadOnlyMemory<float>> YieldChunksAsync(
        IEnumerable<float[]> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (float[] chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<float>> WaitForCancellationAsync(
        TaskCompletionSource waiting,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        waiting.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield return new float[] { 1f };
    }

    [Fact]
    public void PushInputRequiresExplicitValidRateAndFiniteSamples()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationStream.ValidatePushInput([], 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationStream.ValidatePushInput([], 7999, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationStream.ValidatePushInput([], 96001, null));
        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidatePushInput([float.NaN], 16000, null));
        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidatePushInput([float.PositiveInfinity], 16000, null));
        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidatePushInput([float.NegativeInfinity], 16000, null));
    }

    [Fact]
    public void PushInputKeepsRateConstantAndAllowsEmptyNoOp()
    {
        DiarizationStream.ValidatePushInput([], 16000, null);
        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidatePushInput([], 48000, 16000));
        DiarizationStream.ValidatePushInput([-1f, 0f, 1f], 16000, 16000);

        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidatePushInput([0f], 48000, 16000));
    }

    [Fact]
    public async Task ManagedPushValidationFailuresRemainRecoverableAndFinishIsIdempotent()
    {
        int pushCalls = 0;
        int finishCalls = 0;
        using var stream = CreateStream(
            (_, _, _, _) => { pushCalls++; return NativeStatus.Ok; },
            _ => { finishCalls++; return NativeStatus.Ok; });

        Assert.Throws<ArgumentException>(() => stream.Push([float.NaN], 16000));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            stream.PushChunksAsync(YieldChunksAsync([[float.PositiveInfinity]], TestContext.Current.CancellationToken), 16000,
                TestContext.Current.CancellationToken));
        Assert.Equal(0, pushCalls);

        stream.Push([0f], 16000);
        Assert.Equal(1, pushCalls);
        stream.Finish();
        stream.Finish();
        Assert.Equal(1, finishCalls);
        Assert.Throws<InvalidOperationException>(() => stream.Push([1f], 16000));
        Assert.Equal(1, pushCalls);
    }

    [Fact]
    public void NativePushStatusFailureFaultsStreamButAllowsDisposal()
    {
        int pushCalls = 0;
        using var stream = CreateStream(
            (_, _, _, _) => { pushCalls++; return NativeStatus.Runtime; },
            _ => NativeStatus.Ok);

        Assert.Throws<InvalidOperationException>(() => stream.Push([0f], 16000));
        Assert.Equal(1, pushCalls);
        Assert.Throws<InvalidOperationException>(() => stream.Push([1f], 16000));
        Assert.Throws<InvalidOperationException>(() => stream.Finish());
        Assert.Throws<InvalidOperationException>(() => stream.GetSnapshot());
        Assert.Throws<InvalidOperationException>(() => stream.ReadNewFrames());
        Assert.Equal(1, pushCalls);

        stream.Dispose();
        Assert.Throws<ObjectDisposedException>(() => stream.Push([1f], 16000));
    }

    [Fact]
    public void NativeFinishStatusFailureFaultsStreamAndDoesNotRetryFinish()
    {
        int finishCalls = 0;
        using var stream = CreateStream(
            (_, _, _, _) => NativeStatus.Ok,
            _ => { finishCalls++; return NativeStatus.Runtime; });

        Assert.Throws<InvalidOperationException>(() => stream.Finish());
        Assert.Throws<InvalidOperationException>(() => stream.Finish());
        Assert.Throws<InvalidOperationException>(() => stream.Push([0f], 16000));
        Assert.Equal(1, finishCalls);
    }

    [Fact]
    public void InteropExceptionDuringFinishFaultsStreamAndDoesNotRetryFinish()
    {
        var expected = new EntryPointNotFoundException("simulated native entry-point failure");
        int finishCalls = 0;
        using var stream = CreateStream(
            (_, _, _, _) => NativeStatus.Ok,
            _ => { finishCalls++; throw expected; });

        EntryPointNotFoundException observed = Assert.Throws<EntryPointNotFoundException>(
            () => stream.Finish());
        Assert.Same(expected, observed);
        Assert.Throws<InvalidOperationException>(() => stream.Finish());
        Assert.Equal(1, finishCalls);
    }

    [Fact]
    public void InteropExceptionDuringPushFaultsStream()
    {
        var expected = new EntryPointNotFoundException("simulated native entry-point failure");
        using var stream = CreateStream(
            (_, _, _, _) => throw expected,
            _ => NativeStatus.Ok);

        EntryPointNotFoundException observed = Assert.Throws<EntryPointNotFoundException>(
            () => stream.Push([0f], 16000));
        Assert.Same(expected, observed);
        Assert.Throws<InvalidOperationException>(() => stream.Push([1f], 16000));
    }

    [Fact]
    public async Task NativeAsyncPushFailureFaultsStreamAfterOperationCompletes()
    {
        using var stream = CreateStream(
            (_, _, _, _) => NativeStatus.Runtime,
            _ => NativeStatus.Ok);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stream.PushChunksAsync(YieldChunksAsync([[1f]], TestContext.Current.CancellationToken), 16000,
                TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => stream.Finish());
        Assert.Throws<InvalidOperationException>(() => stream.GetSnapshot());
    }

    private static DiarizationStream CreateStream(
        Func<StreamHandle, float[], nuint, int, NativeStatus> push,
        Func<StreamHandle, NativeStatus> finish)
        => new(new StreamHandle(0, new ModelHandle(0)), 2, 0.08, push, finish,
            status => new InvalidOperationException($"simulated native status: {status}"));

    [Fact]
    public void OfflineInputRequiresValidRateAndFiniteSamples()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationStream.ValidateOfflineInput([], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationStream.ValidateOfflineInput([], 7999));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarizationStream.ValidateOfflineInput([], 96001));
        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidateOfflineInput([float.NaN], 16000));
        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidateOfflineInput([float.PositiveInfinity], 16000));
        Assert.Throws<ArgumentException>(() => DiarizationStream.ValidateOfflineInput([float.NegativeInfinity], 16000));

        DiarizationStream.ValidateOfflineInput([], 8000);
        DiarizationStream.ValidateOfflineInput([-1f, 0f, 1f], 96000);
    }

    [Fact]
    public void ModelOptionsPreserveNativeGeometrySentinels()
    {
        var defaults = new DiarModelConfig();
        new DiarizationModelOptions().ApplyNativeOverrides(ref defaults);

        Assert.Equal(0, defaults.ChunkFrames);
        Assert.Equal(0, defaults.RightContextFrames);
        Assert.Equal(-1, defaults.LeftContextFrames);
        Assert.Equal(0, defaults.FifoFrames);
        Assert.Equal(0, defaults.SpeakerCacheFrames);
        Assert.Equal(0, defaults.UpdatePeriodFrames);

        var overrides = new DiarModelConfig();
        new DiarizationModelOptions
        {
            ChunkFrames = 8,
            RightContextFrames = 2,
            LeftContextFrames = 0,
            FifoFrames = 32,
            SpeakerCacheFrames = 40,
            UpdatePeriodFrames = 4
        }.ApplyNativeOverrides(ref overrides);

        Assert.Equal(8, overrides.ChunkFrames);
        Assert.Equal(2, overrides.RightContextFrames);
        Assert.Equal(0, overrides.LeftContextFrames);
        Assert.Equal(32, overrides.FifoFrames);
        Assert.Equal(40, overrides.SpeakerCacheFrames);
        Assert.Equal(4, overrides.UpdatePeriodFrames);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void ModelOptionsMapGpuSelectionWithoutCheckingHardware(int? requestedIndex, int nativeIndex)
    {
        var options = new DiarizationModelOptions { GpuDeviceIndex = requestedIndex };

        Assert.Equal(nativeIndex, options.GetNativeGpuDeviceIndex(DiarizationBackend.Cuda));
    }

    [Fact]
    public void ModelOptionsRejectNegativeGpuIndexesAndKeepCpuAsNull()
    {
        var options = new DiarizationModelOptions { GpuDeviceIndex = -1 };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.GetNativeGpuDeviceIndex(DiarizationBackend.Cuda));
        Assert.Equal(0, new DiarizationModelOptions().GetNativeGpuDeviceIndex(DiarizationBackend.Cuda));
        Assert.Equal(-1, new DiarizationModelOptions { Backend = DiarizationBackend.Cpu }
            .GetNativeGpuDeviceIndex(DiarizationBackend.Cpu));
    }

    [Fact]
    public void BackendIsAutoByDefaultAndExplicitCpuRejectsGpuIndex()
    {
        var defaults = new DiarizationModelOptions();
        Assert.Equal(DiarizationBackend.Auto, defaults.Backend);
        defaults.ValidateBackendOptions();

        var invalid = new DiarizationModelOptions
        {
            Backend = DiarizationBackend.Cpu,
            GpuDeviceIndex = 0
        };
        Assert.Throws<InvalidOperationException>(invalid.ValidateBackendOptions);
    }

    [Fact]
    public void SegmentationOptionsPreserveNativeDefaultSentinelsAndRejectNonFiniteValues()
    {
        NativeSegmentationConfig defaults = new DiarizationSegmentationOptions().ToNativeConfig();
        Assert.Equal((nuint)Marshal.SizeOf<NativeSegmentationConfig>(), defaults.Size);
        Assert.Equal(0f, defaults.Onset);
        Assert.Equal(0f, defaults.Offset);
        Assert.Equal(0d, defaults.PadOnsetSec);
        Assert.Equal(0d, defaults.PadOffsetSec);
        Assert.Equal(0d, defaults.MinGapSec);
        Assert.Equal(0d, defaults.MinDurationSec);

        NativeSegmentationConfig mapped = new DiarizationSegmentationOptions
        {
            Onset = 0.7f,
            Offset = 0.4f,
            PadOnsetSeconds = 0.1,
            PadOffsetSeconds = 0.2,
            MinGapSeconds = 0.3,
            MinDurationSeconds = 0.5
        }.ToNativeConfig();

        Assert.Equal(0.7f, mapped.Onset);
        Assert.Equal(0.4f, mapped.Offset);
        Assert.Equal(0.1, mapped.PadOnsetSec);
        Assert.Equal(0.2, mapped.PadOffsetSec);
        Assert.Equal(0.3, mapped.MinGapSec);
        Assert.Equal(0.5, mapped.MinDurationSec);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiarizationSegmentationOptions { Onset = float.NaN }.ToNativeConfig());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiarizationSegmentationOptions { MinGapSeconds = double.PositiveInfinity }.ToNativeConfig());
    }

    [Fact]
    public void NativeConfigurationStructsMatchThePinnedX64AbiLayout()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(56, Marshal.SizeOf<DiarModelConfig>());
        Assert.Equal(48, Marshal.SizeOf<NativeSegmentationConfig>());

        Assert.Equal(0, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.Size)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.ModelPath)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.Gpu)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.Preset)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.ChunkFrames)).ToInt32());
        Assert.Equal(36, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.RightContextFrames)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.LeftContextFrames)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.FifoFrames)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.SpeakerCacheFrames)).ToInt32());
        Assert.Equal(52, Marshal.OffsetOf<DiarModelConfig>(nameof(DiarModelConfig.UpdatePeriodFrames)).ToInt32());
        Assert.Equal(0, Marshal.OffsetOf<NativeSegmentationConfig>(nameof(NativeSegmentationConfig.Size)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<NativeSegmentationConfig>(nameof(NativeSegmentationConfig.Onset)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<NativeSegmentationConfig>(nameof(NativeSegmentationConfig.Offset)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<NativeSegmentationConfig>(nameof(NativeSegmentationConfig.PadOnsetSec)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<NativeSegmentationConfig>(nameof(NativeSegmentationConfig.PadOffsetSec)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<NativeSegmentationConfig>(nameof(NativeSegmentationConfig.MinGapSec)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<NativeSegmentationConfig>(nameof(NativeSegmentationConfig.MinDurationSec)).ToInt32());
    }

    [Fact]
    public void LoadRejectsMissingModelBeforeNativeLoading()
    {
        Assert.Throws<FileNotFoundException>(() => DiarizationModel.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".gguf")));
    }

    [Fact]
    public void NativeModelMetadataMustFitManagedResults()
    {
        DiarizationModel.ValidateNativeMetadata(4, 0.08);

        Assert.Throws<InvalidOperationException>(() => DiarizationModel.ValidateNativeMetadata(0, 0.08));
        Assert.Throws<InvalidOperationException>(() => DiarizationModel.ValidateNativeMetadata(-1, 0.08));
        Assert.Throws<InvalidOperationException>(() => DiarizationModel.ValidateNativeMetadata(int.MaxValue, 0.08));
        Assert.Throws<InvalidOperationException>(() => DiarizationModel.ValidateNativeMetadata(4, 0));
        Assert.Throws<InvalidOperationException>(() => DiarizationModel.ValidateNativeMetadata(4, double.NaN));
        Assert.Throws<InvalidOperationException>(() => DiarizationModel.ValidateNativeMetadata(4, double.PositiveInfinity));
    }
}
