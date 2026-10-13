using System.Runtime.CompilerServices;
using NemotronDiarizationSharp;
using NemotronDiarizationSharp.Native;
using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class AsyncResultsTests
{
    [Fact]
    public async Task YieldsOwnedUpdatePerChunkIncludingEmptyChunksAndFinalUpdate()
    {
        var fixture = new Fixture();
        int disposed = 0;
        float[] reusable = [0.2f];
        async IAsyncEnumerable<ReadOnlyMemory<float>> Source()
        {
            yield return reusable;
            yield return ReadOnlyMemory<float>.Empty;
            reusable[0] = 0.8f;
            yield return reusable;
            await Task.CompletedTask;
            disposed++;
        }

        try
        {
            var updates = new List<DiarizationResultUpdate>();
            await foreach (DiarizationResultUpdate update in fixture.Stream.ProcessStreamAsync(Source(), 16000))
                updates.Add(update);

            Assert.Equal(4, updates.Count);
            Assert.Equal(new long[] { 1, 2, 3, 4 }, updates.Select(x => x.Sequence));
            Assert.All(updates, update => Assert.Equal(updates[0].SourceId, update.SourceId));
            Assert.Equal(new[] { 0.2f }, updates[0].NewFrames.Probabilities);
            Assert.Empty(updates[1].NewFrames.Probabilities);
            Assert.Equal(new[] { 0.8f }, updates[2].NewFrames.Probabilities);
            Assert.True(updates[3].IsFinal);
            Assert.Empty(updates[3].NewFrames.Probabilities);
            Assert.Equal(new[] { 0.2f }, updates[0].NewFrames.Probabilities);
            Assert.Equal(2, fixture.PushCount);
            Assert.Equal(new[] { 16000, 16000 }, fixture.SampleRates);
            Assert.Equal(1, fixture.FinishCount);
            Assert.Equal(1, disposed);
            Assert.Throws<InvalidOperationException>(() => fixture.Stream.Push([0.1f], 16000));
        }
        finally
        {
            fixture.Invalidate();
        }
    }

    [Fact]
    public async Task PullBackpressureAndReservationLastThroughYieldAndEarlyDisposal()
    {
        var fixture = new Fixture();
        var firstRequested = NewSignal();
        var secondRequested = NewSignal();
        var releaseSecond = NewSignal();
        async IAsyncEnumerable<ReadOnlyMemory<float>> Source()
        {
            firstRequested.SetResult();
            yield return new float[] { 0.1f };
            secondRequested.SetResult();
            await releaseSecond.Task;
            yield return new float[] { 0.2f };
        }

        try
        {
            IAsyncEnumerator<DiarizationResultUpdate> enumerator = fixture.Stream
                .ProcessStreamAsync(Source(), 16000).GetAsyncEnumerator();
            try
            {
                Assert.True(await enumerator.MoveNextAsync());
                await firstRequested.Task.WaitAsync(Watchdog);
                Assert.False(secondRequested.Task.IsCompleted);
                Assert.Throws<InvalidOperationException>(() => fixture.Stream.Push([0.3f], 16000));
                Assert.Throws<InvalidOperationException>(() => fixture.Stream.Finish());
                Assert.Throws<InvalidOperationException>(() => fixture.Stream.ReadResultUpdates());
                Assert.Throws<InvalidOperationException>(() => fixture.Stream.Dispose());

                IAsyncEnumerator<DiarizationResultUpdate> competing = fixture.Stream
                    .ProcessStreamAsync(EmptySource(), 16000).GetAsyncEnumerator();
                try
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(async () => await competing.MoveNextAsync());
                }
                finally
                {
                    await competing.DisposeAsync();
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            Assert.Equal(1, fixture.PushCount);
            Assert.Equal(0, fixture.FinishCount);
            fixture.Stream.Push([0.4f], 16000);
            Assert.Equal(2, fixture.PushCount);
        }
        finally
        {
            releaseSecond.TrySetResult();
            fixture.Invalidate();
        }
    }

    [Fact]
    public async Task EmptySourceDisposesBeforeFinishingAndYieldsOneFinalResult()
    {
        var fixture = new Fixture();
        var sourceDisposed = false;
        async IAsyncEnumerable<ReadOnlyMemory<float>> Source()
        {
            try { await Task.CompletedTask; yield break; }
            finally { sourceDisposed = true; }
        }
        fixture.Finish = _ =>
        {
            Assert.True(sourceDisposed);
            fixture.FinishCount++;
            return NativeStatus.Ok;
        };

        try
        {
            await using IAsyncEnumerator<DiarizationResultUpdate> enumerator = fixture.Stream
                .ProcessStreamAsync(Source(), 16000).GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());
            Assert.True(enumerator.Current.IsFinal);
            Assert.Equal(1, enumerator.Current.Sequence);
            Assert.False(await enumerator.MoveNextAsync());
            Assert.Equal(1, fixture.FinishCount);
        }
        finally
        {
            fixture.Invalidate();
        }
    }

    [Fact]
    public async Task PreCancellationAndInvalidStartupDoNotReserveOrMutate()
    {
        var fixture = new Fixture();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            IAsyncEnumerator<DiarizationResultUpdate> canceledEnumerator = fixture.Stream
                .ProcessStreamAsync(EmptySource(), 16000, canceled.Token).GetAsyncEnumerator();
            await using (canceledEnumerator)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledEnumerator.MoveNextAsync());
            Assert.Equal(0, fixture.PushCount);
            Assert.Equal(0, fixture.FinishCount);

            IAsyncEnumerator<DiarizationResultUpdate> invalidRate = fixture.Stream
                .ProcessStreamAsync(EmptySource(), 7999).GetAsyncEnumerator();
            await using (invalidRate)
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await invalidRate.MoveNextAsync());

            fixture.Stream.Push([0.4f], 16000);
            Assert.Equal(1, fixture.PushCount);
            fixture.Stream.Finish();
            IAsyncEnumerator<DiarizationResultUpdate> finished = fixture.Stream
                .ProcessStreamAsync(EmptySource(), 16000).GetAsyncEnumerator();
            await using (finished)
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await finished.MoveNextAsync());
        }
        finally
        {
            fixture.Invalidate();
        }
    }

    [Fact]
    public async Task CancellationDuringAcquiredChunkYieldsCommittedResultThenStopsBeforeFinish()
    {
        var pushStarted = NewSignal();
        var releasePush = NewSignal();
        List<float> recordedProbabilities = [];
        var fixture = new Fixture(push: (_, samples, _, _) =>
        {
            pushStarted.TrySetResult();
            releasePush.Task.GetAwaiter().GetResult();
            recordedProbabilities.Add(samples[0]);
            return NativeStatus.Ok;
        }, probabilities: () => recordedProbabilities.ToArray());
        using var cancellation = new CancellationTokenSource();
        async IAsyncEnumerable<ReadOnlyMemory<float>> Source()
        {
            yield return new float[] { 0.65f };
            await Task.Yield();
            yield return new float[] { 0.75f };
        }

        try
        {
            await using IAsyncEnumerator<DiarizationResultUpdate> enumerator = fixture.Stream
                .ProcessStreamAsync(Source(), 16000, cancellation.Token).GetAsyncEnumerator();
            Task<bool> moving = enumerator.MoveNextAsync().AsTask();
            await pushStarted.Task.WaitAsync(Watchdog);
            cancellation.Cancel();
            releasePush.SetResult();
            Assert.True(await moving.WaitAsync(Watchdog));
            Assert.Equal(new[] { 0.65f }, enumerator.Current.NewFrames.Probabilities);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
            Assert.Equal(1, fixture.PushCount);
            Assert.Equal(0, fixture.FinishCount);
            fixture.Stream.Push([0.9f], 16000);
        }
        finally
        {
            releasePush.TrySetResult();
            fixture.Invalidate();
        }
    }

    [Fact]
    public async Task EnumeratorCancellationTokenIsForwardedToSource()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        CancellationToken received = default;
        async IAsyncEnumerable<ReadOnlyMemory<float>> Source(
            [EnumeratorCancellation] CancellationToken token = default)
        {
            received = token;
            await Task.CompletedTask;
            yield break;
        }

        try
        {
            await foreach (DiarizationResultUpdate _ in fixture.Stream
                .ProcessStreamAsync(Source(), 16000).WithCancellation(cancellation.Token)) { }
            Assert.Equal(cancellation.Token, received);
        }
        finally
        {
            fixture.Invalidate();
        }
    }

    [Fact]
    public async Task CancellationBetweenResultAndFinishPreventsFinish()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        async IAsyncEnumerable<ReadOnlyMemory<float>> Source()
        {
            yield return new float[] { 0.5f };
            await Task.CompletedTask;
        }

        try
        {
            await using IAsyncEnumerator<DiarizationResultUpdate> enumerator = fixture.Stream
                .ProcessStreamAsync(Source(), 16000, cancellation.Token).GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
            Assert.Equal(0, fixture.FinishCount);
            fixture.Stream.Push([0.7f], 16000);
        }
        finally
        {
            fixture.Invalidate();
        }
    }

    [Fact]
    public async Task CooperativeCancellationWhileSourceWaitsReleasesReservationWithoutFinish()
    {
        var fixture = new Fixture();
        var sourceEntered = NewSignal();
        using var cancellation = new CancellationTokenSource();
        async IAsyncEnumerable<ReadOnlyMemory<float>> Source(
            [EnumeratorCancellation] CancellationToken token = default)
        {
            sourceEntered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            yield break;
        }

        try
        {
            IAsyncEnumerator<DiarizationResultUpdate> enumerator = fixture.Stream
                .ProcessStreamAsync(Source(), 16000, cancellation.Token).GetAsyncEnumerator();
            Task<bool> moving = enumerator.MoveNextAsync().AsTask();
            await sourceEntered.Task.WaitAsync(Watchdog);
            Assert.Throws<InvalidOperationException>(() => fixture.Stream.Push([0.2f], 16000));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
            await enumerator.DisposeAsync();
            Assert.Equal(0, fixture.FinishCount);
            fixture.Stream.Push([0.6f], 16000);
        }
        finally
        {
            fixture.Invalidate();
        }
    }

    [Fact]
    public async Task CancellationDuringSuccessfulFinishStillYieldsFinalAndCompletesNormally()
    {
        var finishStarted = NewSignal();
        var releaseFinish = NewSignal();
        using var cancellation = new CancellationTokenSource();
        int finishCalls = 0;
        var fixture = new Fixture(finish: _ =>
        {
            Interlocked.Increment(ref finishCalls);
            finishStarted.SetResult();
            releaseFinish.Task.GetAwaiter().GetResult();
            return NativeStatus.Ok;
        });

        try
        {
            await using IAsyncEnumerator<DiarizationResultUpdate> enumerator = fixture.Stream
                .ProcessStreamAsync(EmptySource(), 16000, cancellation.Token).GetAsyncEnumerator();
            Task<bool> moving = enumerator.MoveNextAsync().AsTask();
            await finishStarted.Task.WaitAsync(Watchdog);
            cancellation.Cancel();
            releaseFinish.SetResult();
            Assert.True(await moving.WaitAsync(Watchdog));
            Assert.True(enumerator.Current.IsFinal);
            Assert.False(await enumerator.MoveNextAsync());
            Assert.Equal(1, finishCalls);
        }
        finally
        {
            releaseFinish.TrySetResult();
            fixture.Invalidate();
        }
    }

    [Fact]
    public async Task SourceFailureAndDisposalFailureReleaseReservationWithoutFinish()
    {
        var fixture = new Fixture();
        int disposed = 0;
        async IAsyncEnumerable<ReadOnlyMemory<float>> FailingSource()
        {
            try
            {
                yield return new float[] { 0.3f };
                throw new TestSourceException();
            }
            finally { disposed++; }
        }

        try
        {
            await using (IAsyncEnumerator<DiarizationResultUpdate> enumerator = fixture.Stream
                .ProcessStreamAsync(FailingSource(), 16000).GetAsyncEnumerator())
            {
                Assert.True(await enumerator.MoveNextAsync());
                await Assert.ThrowsAsync<TestSourceException>(async () => await enumerator.MoveNextAsync());
            }
            Assert.Equal(1, disposed);
            Assert.Equal(1, fixture.PushCount);
            Assert.Equal(0, fixture.FinishCount);
            fixture.Stream.Push([0.4f], 16000);

            IAsyncEnumerator<DiarizationResultUpdate> acquisitionFailure = fixture.Stream
                .ProcessStreamAsync(new CustomSource(() => throw new TestSourceException()), 16000).GetAsyncEnumerator();
            await Assert.ThrowsAsync<TestSourceException>(async () => await acquisitionFailure.MoveNextAsync());
            await acquisitionFailure.DisposeAsync();
            fixture.Stream.Push([0.45f], 16000);

            CurrentThrowingEnumerator? inner = null;
            var source = new CustomSource(() => inner = new CurrentThrowingEnumerator());
            IAsyncEnumerator<DiarizationResultUpdate> result = fixture.Stream.ProcessStreamAsync(source, 16000).GetAsyncEnumerator();
            await Assert.ThrowsAsync<TestSourceException>(async () => await result.MoveNextAsync());
            Assert.Equal(1, inner!.DisposeCount);
            await result.DisposeAsync();
            fixture.Stream.Push([0.5f], 16000);
        }
        finally
        {
            fixture.Invalidate();
        }
    }

    [Fact]
    public async Task SourceDisposeFailureDoesNotFinishAndStillReleasesReservation()
    {
        var fixture = new Fixture();
        try
        {
            var sourceEnumerator = new DisposeThrowingEnumerator();
            IAsyncEnumerator<DiarizationResultUpdate> enumerator = fixture.Stream
                .ProcessStreamAsync(new CustomSource(() => sourceEnumerator), 16000).GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());
            await Assert.ThrowsAsync<TestSourceException>(() => enumerator.DisposeAsync().AsTask());
            Assert.Equal(1, sourceEnumerator.DisposeCount);
            Assert.Equal(0, fixture.FinishCount);
            fixture.Stream.Push([0.5f], 16000);
        }
        finally
        {
            fixture.Invalidate();
        }
    }

    [Fact]
    public async Task NativePushAndFinishFailuresFaultStreamWithoutRetryingMutation()
    {
        int pushes = 0;
        int finishes = 0;
        var fixture = new Fixture(
            push: (_, _, _, _) => ++pushes == 1 ? NativeStatus.Runtime : NativeStatus.Ok,
            finish: _ => { finishes++; return NativeStatus.Runtime; });
        try
        {
            IAsyncEnumerator<DiarizationResultUpdate> pushEnumerator = fixture.Stream
                .ProcessStreamAsync(OneChunk(0.4f), 16000).GetAsyncEnumerator();
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await pushEnumerator.MoveNextAsync());
            await pushEnumerator.DisposeAsync();
            Assert.Equal(1, pushes);
            Assert.Throws<InvalidOperationException>(() => fixture.Stream.ReadResultUpdates());
            Assert.Throws<InvalidOperationException>(() => fixture.Stream.Push([0.5f], 16000));
            Assert.Equal(0, finishes);
        }
        finally
        {
            fixture.Invalidate();
        }

        fixture = new Fixture(finish: _ => { finishes++; return NativeStatus.Runtime; });
        try
        {
            IAsyncEnumerator<DiarizationResultUpdate> finishEnumerator = fixture.Stream
                .ProcessStreamAsync(EmptySource(), 16000).GetAsyncEnumerator();
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await finishEnumerator.MoveNextAsync());
            await finishEnumerator.DisposeAsync();
            Assert.Equal(1, finishes);
            Assert.Throws<InvalidOperationException>(() => fixture.Stream.Finish());
            Assert.Equal(1, finishes);
        }
        finally
        {
            fixture.Invalidate();
        }
    }

    [Fact]
    public async Task ReadFailureAfterPushAndHistoryGapAreRecoverableWithoutReplayingPcm()
    {
        int reads = 0;
        var fixture = new Fixture(read: (_, buffer, _) =>
        {
            if (buffer.Length > 0) buffer[0] = 0.3f;
            return ++reads == 1 ? NativeStatus.Runtime : NativeStatus.Ok;
        });
        try
        {
            IAsyncEnumerator<DiarizationResultUpdate> enumerator = fixture.Stream
                .ProcessStreamAsync(OneChunk(0.3f), 16000).GetAsyncEnumerator();
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await enumerator.MoveNextAsync());
            await enumerator.DisposeAsync();
            Assert.Equal(1, fixture.PushCount);
            Assert.Equal(1, fixture.Stream.ReadResultUpdates().Sequence);
            Assert.Equal(1, fixture.PushCount);

            fixture.TotalFrames = 3;
            fixture.RetainedStart = 2;
            IAsyncEnumerator<DiarizationResultUpdate> gapEnumerator = fixture.Stream
                .ProcessStreamAsync(OneChunk(0.4f), 16000).GetAsyncEnumerator();
            await Assert.ThrowsAsync<DiarizationFrameHistoryGapException>(async () => await gapEnumerator.MoveNextAsync());
            await gapEnumerator.DisposeAsync();
            Assert.Equal(2, fixture.PushCount);
            Assert.Equal(new DiarizationFrameRange(1, 2), fixture.Stream.ReadResultUpdates(allowHistoryGap: true).HistoryGap);
            fixture.RetainedStart = 0;
            await foreach (DiarizationResultUpdate update in fixture.Stream.ProcessStreamAsync(EmptySource(), 16000))
                Assert.True(update.IsFinal);
        }
        finally
        {
            fixture.Invalidate();
        }
    }

    [Fact]
    public async Task SegmentReadFailureIsRecoverableWithoutReplayingPcm()
    {
        bool failSegments = true;
        var fixture = new Fixture(segments: () =>
        {
            if (failSegments) throw new InvalidOperationException("Fake segment read failure.");
            return [];
        });
        try
        {
            IAsyncEnumerator<DiarizationResultUpdate> enumerator = fixture.Stream
                .ProcessStreamAsync(OneChunk(0.35f), 16000).GetAsyncEnumerator();
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await enumerator.MoveNextAsync());
            Assert.Equal("Fake segment read failure.", error.Message);
            await enumerator.DisposeAsync();
            Assert.Equal(1, fixture.PushCount);

            failSegments = false;
            Assert.Equal(1, fixture.Stream.ReadResultUpdates().Sequence);
            Assert.Equal(1, fixture.PushCount);
        }
        finally
        {
            fixture.Invalidate();
        }
    }

    [Fact]
    public async Task SuccessfulFinishWithFailedFinalReadCanRecoverWithoutRetryingFinish()
    {
        int finishCount = 0;
        int reads = 0;
        var fixture = new Fixture(
            finish: _ => { finishCount++; return NativeStatus.Ok; },
            read: (_, buffer, _) =>
            {
                if (buffer.Length > 0) buffer[0] = 0.2f;
                return ++reads == 1 ? NativeStatus.Runtime : NativeStatus.Ok;
            });
        try
        {
            fixture.Stream.Push([0.2f], 16000);
            IAsyncEnumerator<DiarizationResultUpdate> enumerator = fixture.Stream
                .ProcessStreamAsync(EmptySource(), 16000).GetAsyncEnumerator();
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await enumerator.MoveNextAsync());
            await enumerator.DisposeAsync();
            DiarizationResultUpdate recovered = fixture.Stream.ReadResultUpdates();
            Assert.True(recovered.IsFinal);
            Assert.Equal(1, finishCount);
            fixture.Stream.Finish();
            Assert.Equal(1, finishCount);
        }
        finally
        {
            fixture.Invalidate();
        }
    }

    [Fact]
    public async Task EarlierManualReadsShareCursorAndDifferentStreamsAreIndependent()
    {
        var first = new Fixture();
        var second = new Fixture();
        try
        {
            Assert.Equal(1, first.Stream.ReadResultUpdates().Sequence);
            DiarizationResultUpdate fromAsync = await FirstAsync(first.Stream.ProcessStreamAsync(EmptySource(), 16000));
            Assert.Equal(2, fromAsync.Sequence);
            DiarizationResultUpdate other = await FirstAsync(second.Stream.ProcessStreamAsync(EmptySource(), 16000));
            Assert.Equal(1, other.Sequence);
            Assert.NotEqual(fromAsync.SourceId, other.SourceId);
        }
        finally
        {
            first.Invalidate();
            second.Invalidate();
        }
    }

    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async IAsyncEnumerable<ReadOnlyMemory<float>> EmptySource()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<float>> OneChunk(float sample)
    {
        await Task.CompletedTask;
        yield return new[] { sample };
    }

    private static async Task<DiarizationResultUpdate> FirstAsync(IAsyncEnumerable<DiarizationResultUpdate> updates)
    {
        await using IAsyncEnumerator<DiarizationResultUpdate> enumerator = updates.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    private sealed class Fixture
    {
        private readonly List<float> _probabilities = [];
        public readonly StreamHandle Handle;
        public DiarizationStream Stream { get; }
        public int PushCount { get; private set; }
        public int FinishCount { get; set; }
        public List<int> SampleRates { get; } = [];
        public long TotalFrames { get; set; }
        public long RetainedStart { get; set; }
        public Func<StreamHandle, float[], nuint, int, NativeStatus> Push { get; set; }
        public Func<StreamHandle, NativeStatus> Finish { get; set; }
        public Func<DiarizationSegment[]> Segments { get; set; }

        public Fixture(
            Func<StreamHandle, float[], nuint, int, NativeStatus>? push = null,
            Func<StreamHandle, NativeStatus>? finish = null,
            Func<StreamHandle, float[], nuint, NativeStatus>? read = null,
            Func<float[]>? probabilities = null,
            Func<DiarizationSegment[]>? segments = null)
        {
            Handle = new StreamHandle(1, new ModelHandle(0));
            Push = push ?? ((_, samples, _, _) =>
            {
                PushCount++;
                _probabilities.AddRange(samples);
                TotalFrames += samples.Length;
                return NativeStatus.Ok;
            });
            Finish = finish ?? (_ => { FinishCount++; return NativeStatus.Ok; });
            Segments = segments ?? (() => []);
            Stream = new DiarizationStream(Handle, 1, 0.08,
                (handle, samples, count, rate) =>
                {
                    PushCount++;
                    SampleRates.Add(rate);
                    if (push is null)
                    {
                        _probabilities.AddRange(samples);
                        TotalFrames += (long)count;
                        return NativeStatus.Ok;
                    }
                    NativeStatus status = Push(handle, samples, count, rate);
                    if (status == NativeStatus.Ok)
                    {
                        _probabilities.AddRange(samples);
                        TotalFrames += (long)count;
                    }
                    return status;
                },
                (handle) => Finish(handle),
                status => new InvalidOperationException($"Fake native mutation failed: {status}."),
                _ => TotalFrames,
                _ => RetainedStart,
                (handle, buffer, _) =>
                {
                    if (read is not null)
                        return read(handle, buffer, (nuint)buffer.Length);
                    float[] current = probabilities?.Invoke() ?? _probabilities.ToArray();
                    int copyLength = Math.Min(current.Length, buffer.Length);
                    if (copyLength > 0)
                    {
                        int sourceStart = current.Length > buffer.Length ? current.Length - buffer.Length : 0;
                        Array.Copy(current, sourceStart, buffer, 0, copyLength);
                    }
                    return NativeStatus.Ok;
                },
                _ => Segments());
        }

        public void Invalidate() => Handle.SetHandleAsInvalid();
    }

    private sealed class TestSourceException : Exception { }

    private sealed class CustomSource(Func<IAsyncEnumerator<ReadOnlyMemory<float>>> factory)
        : IAsyncEnumerable<ReadOnlyMemory<float>>
    {
        public IAsyncEnumerator<ReadOnlyMemory<float>> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => factory();
    }

    private sealed class CurrentThrowingEnumerator : IAsyncEnumerator<ReadOnlyMemory<float>>
    {
        public int DisposeCount { get; private set; }
        public ReadOnlyMemory<float> Current => throw new TestSourceException();
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(true);
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class DisposeThrowingEnumerator : IAsyncEnumerator<ReadOnlyMemory<float>>
    {
        private bool _yielded;
        public int DisposeCount { get; private set; }
        public ReadOnlyMemory<float> Current => new float[] { 0.3f };
        public ValueTask<bool> MoveNextAsync()
        {
            if (_yielded) return ValueTask.FromResult(false);
            _yielded = true;
            return ValueTask.FromResult(true);
        }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.FromException(new TestSourceException()); }
    }
}
