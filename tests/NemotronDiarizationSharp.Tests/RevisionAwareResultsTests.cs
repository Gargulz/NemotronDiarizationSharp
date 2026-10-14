using NemotronDiarizationSharp;
using NemotronDiarizationSharp.Native;
using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class RevisionAwareResultsTests
{
    [Fact]
    public void ReportsNewFramesAndDisjointRevisionsWithOwnedValues()
    {
        var cursor = new ResultUpdateCursor();
        DiarizationResultUpdate first = Read(cursor, 0, 5, [0.1f, 0.2f, 0.3f, 0.4f, 0.5f], [Seg(0)]);
        Assert.Equal(1, first.Sequence);
        Assert.Equal(0, first.NewFrames.FirstFrameIndex);
        Assert.Equal(5, first.NewFrames.FrameCount);
        Assert.Null(first.ComparedFrames);

        float[] revisedWindow = [0.1f, 0.8f, 0.3f, 0.7f, 0.5f, 0.9f, 0.6f];
        DiarizationResultUpdate second = Read(cursor, 0, 7, revisedWindow, [Seg(1)]);

        Assert.Equal(first.SourceId, second.SourceId);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(new DiarizationFrameRange(0, 5), second.ComparedFrames);
        Assert.Equal(2, second.RevisedFrames.Count);
        Assert.Equal(1, second.RevisedFrames[0].FirstFrameIndex);
        Assert.Equal(new[] { 0.8f }, second.RevisedFrames[0].Probabilities);
        Assert.Equal(3, second.RevisedFrames[1].FirstFrameIndex);
        Assert.Equal(new[] { 0.7f }, second.RevisedFrames[1].Probabilities);
        Assert.Equal(5, second.NewFrames.FirstFrameIndex);
        Assert.Equal(new[] { 0.9f, 0.6f }, second.NewFrames.Probabilities);
        Assert.Equal(new[] { Seg(1) }, second.Segments);

        revisedWindow[1] = 0.01f;
        Assert.Equal(0.8f, second.RevisedFrames[0].GetProbability(1, 0));
        Assert.Equal(0.9f, second.NewFrames.GetProbability(5, 0));
        Assert.Equal(Seg(0), first.Segments[0]);

        DiarizationResultUpdate third = Read(cursor, 0, 7, revisedWindow, [Seg(2)]);
        Assert.Equal(0.8f, second.RevisedFrames[0].GetProbability(1, 0));
        Assert.Equal(Seg(1), second.Segments[0]);
        Assert.Equal(3, third.Sequence);
    }

    [Fact]
    public void EmptyUnchangedUpdateHasComparisonAndFinalityOnlyAfterFinish()
    {
        var cursor = new ResultUpdateCursor();
        DiarizationResultUpdate first = Read(cursor, 0, 2, [0.2f, 0.3f], [], isFinal: false);
        DiarizationResultUpdate second = Read(cursor, 0, 2, [0.2f, 0.3f], [], isFinal: true);

        Assert.False(first.IsFinal);
        Assert.True(second.IsFinal);
        Assert.Equal(2, second.Sequence);
        Assert.Empty(second.NewFrames.Probabilities);
        Assert.Empty(second.RevisedFrames);
        Assert.Equal(new DiarizationFrameRange(0, 2), second.ComparedFrames);
    }

    [Fact]
    public void GroupsAdjacentMultiSpeakerRevisionsAndComparesOnlyRetainedTail()
    {
        var cursor = new ResultUpdateCursor();
        float[] initial = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 0.1f];
        _ = Read(cursor, 0, 5, 2, initial);

        float[] revised = [0.1f, 0.2f, 0.9f, 0.4f, 0.5f, 0.7f, 0.7f, 0.8f, 0.8f, 0.1f];
        DiarizationResultUpdate update = Read(cursor, 0, 5, 2, revised);
        Assert.Equal(new DiarizationFrameRange(0, 5), update.ComparedFrames);
        Assert.Equal(2, update.RevisedFrames.Count);
        Assert.Equal(1, update.RevisedFrames[0].FirstFrameIndex);
        Assert.Equal(2, update.RevisedFrames[0].FrameCount);
        Assert.Equal(new[] { 0.9f, 0.4f, 0.5f, 0.7f }, update.RevisedFrames[0].Probabilities);
        Assert.Equal(4, update.RevisedFrames[1].FirstFrameIndex);
        Assert.Equal(new[] { 0.8f, 0.1f }, update.RevisedFrames[1].Probabilities);

        float[] compactedTail = [0.5f, 0.7f, 0.7f, 0.8f, 0.6f, 0.1f, 0.3f, 0.4f, 0.2f, 0.3f];
        DiarizationResultUpdate tail = Read(cursor, 2, 7, 2, compactedTail);
        Assert.Equal(new DiarizationFrameRange(2, 5), tail.ComparedFrames);
        Assert.Null(tail.HistoryGap);
        Assert.Equal(5, tail.NewFrames.FirstFrameIndex);
        Assert.Equal(new[] { 0.3f, 0.4f, 0.2f, 0.3f }, tail.NewFrames.Probabilities);
        Assert.Single(tail.RevisedFrames);
        Assert.Equal(4, tail.RevisedFrames[0].FirstFrameIndex);
        Assert.Equal(new[] { 0.6f, 0.1f }, tail.RevisedFrames[0].Probabilities);
    }

    [Fact]
    public void StrictHistoryGapRepeatsAndExplicitRecoveryReportsExactGap()
    {
        var cursor = new ResultUpdateCursor();
        _ = Read(cursor, 0, 2, [0.2f, 0.3f], []);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            DiarizationFrameHistoryGapException gap = Assert.Throws<DiarizationFrameHistoryGapException>(
                () => Read(cursor, 4, 6, [0.4f, 0.5f], []));
            Assert.Equal(2, gap.MissingStartFrame);
            Assert.Equal(4, gap.MissingEndFrameExclusive);
        }
        Assert.Equal(1, cursor.Sequence);
        Assert.Equal(2, cursor.NextUnreadFrame);

        DiarizationResultUpdate recovered = Read(cursor, 4, 6, [0.4f, 0.5f], [], allowHistoryGap: true);
        Assert.Equal(new DiarizationFrameRange(2, 4), recovered.HistoryGap);
        Assert.Equal(4, recovered.NewFrames.FirstFrameIndex);
        Assert.Equal(2, recovered.NewFrames.FrameCount);
        Assert.Null(recovered.ComparedFrames);
        Assert.Equal(2, recovered.Sequence);
        Assert.Equal(6, cursor.NextUnreadFrame);
    }

    [Fact]
    public void FailedRecoveryDoesNotAdvanceSequenceOrReplaceComparisonBaseline()
    {
        var cursor = new ResultUpdateCursor();
        _ = Read(cursor, 0, 2, [0.2f, 0.3f], []);

        InvalidOperationException failedRead = Assert.Throws<InvalidOperationException>(() => cursor.Read(4, 6, 1, 0.08, false, true,
            (_, _) => NativeStatus.Runtime, () => [], FakeNativeError));
        Assert.Equal("Fake native read failed: Runtime.", failedRead.Message);
        Assert.Equal(1, cursor.Sequence);
        Assert.Equal(2, cursor.NextUnreadFrame);

        DiarizationResultUpdate recovered = Read(cursor, 4, 6, [0.4f, 0.5f], [], allowHistoryGap: true);
        Assert.Equal(2, recovered.Sequence);
        Assert.Equal(new DiarizationFrameRange(2, 4), recovered.HistoryGap);
    }

    [Fact]
    public void CacheAndScratchReuseCapacityAcrossUnchangedReadsAndCompaction()
    {
        var cursor = new ResultUpdateCursor();
        _ = Read(cursor, 0, 4, [0.1f, 0.2f, 0.3f, 0.4f], []);
        _ = Read(cursor, 0, 4, [0.1f, 0.2f, 0.3f, 0.4f], []);
        int cacheCapacity = cursor.CacheCapacity;
        int scratchCapacity = cursor.ScratchCapacity;
        (object cacheIdentity, object scratchIdentity) = cursor.BufferIdentityForTesting;

        _ = Read(cursor, 0, 4, [0.1f, 0.2f, 0.3f, 0.4f], []);
        Assert.Equal(cacheCapacity, cursor.CacheCapacity);
        Assert.Equal(scratchCapacity, cursor.ScratchCapacity);
        (object cacheIdentityAfterRead, object scratchIdentityAfterRead) = cursor.BufferIdentityForTesting;
        Assert.Same(scratchIdentity, cacheIdentityAfterRead);
        Assert.Same(cacheIdentity, scratchIdentityAfterRead);

        _ = Read(cursor, 2, 4, [0.3f, 0.4f], [], allowHistoryGap: true);
        Assert.Equal(cacheCapacity, cursor.CacheCapacity);
        Assert.Equal(scratchCapacity, cursor.ScratchCapacity);
    }

    [Fact]
    public void ReadFailuresMalformedValuesMetadataAndFrameRegressionDoNotCommit()
    {
        var cursor = new ResultUpdateCursor();
        _ = Read(cursor, 0, 2, [0.2f, 0.3f], []);

        Assert.Throws<InvalidOperationException>(() => cursor.Read(0, 3, 1, 0.08, false, false,
            (buffer, count) => { buffer[0] = 0.5f; return NativeStatus.Ok; }, () => throw new InvalidOperationException(), FakeNativeError));
        Assert.Throws<InvalidOperationException>(() => Read(cursor, 0, 3, [0.2f, float.NaN, 0.4f], []));
        Assert.Throws<InvalidOperationException>(() => cursor.Read(0, 3, 1, 0.09, false, false,
            (_, _) => NativeStatus.Ok, () => [], FakeNativeError));
        Assert.Throws<InvalidOperationException>(() => cursor.Read(0, 1, 1, 0.08, false, false,
            (_, _) => NativeStatus.Ok, () => [], FakeNativeError));

        Assert.Equal(1, cursor.Sequence);
        Assert.Equal(2, cursor.NextUnreadFrame);
        DiarizationResultUpdate retry = Read(cursor, 0, 3, [0.2f, 0.3f, 0.4f], []);
        Assert.Equal(2, retry.Sequence);
        Assert.Empty(retry.RevisedFrames);
    }

    [Fact]
    public void MalformedSegmentsBoundsSpeakersAndArithmeticRollbackTheCursor()
    {
        var cursor = new ResultUpdateCursor();
        _ = Read(cursor, 0, 1, [0.2f], []);

        Assert.Throws<InvalidOperationException>(() => ReadWithSegments(cursor, 0, 2,
            [0.2f, 0.3f], [new DiarizationSegment(2, 3, 1), new DiarizationSegment(1, 2, 1)]));
        Assert.Throws<InvalidOperationException>(() => ReadWithSegments(cursor, 0, 2,
            [0.2f, 0.3f], [new DiarizationSegment(0, 1, 0)]));
        Assert.Throws<InvalidOperationException>(() => cursor.Read(-1, 1, 1, 0.08, false, false,
            (_, _) => NativeStatus.Ok, () => [], FakeNativeError));
        Assert.Throws<InvalidOperationException>(() => cursor.Read(2, 1, 1, 0.08, false, false,
            (_, _) => NativeStatus.Ok, () => [], FakeNativeError));
        Assert.Throws<InvalidOperationException>(() => cursor.Read(0, long.MaxValue, 2, 0.08, false, false,
            (_, _) => NativeStatus.Ok, () => [], FakeNativeError));

        Assert.Equal(1, cursor.Sequence);
        Assert.Equal(1, cursor.NextUnreadFrame);
        DiarizationResultUpdate retry = Read(cursor, 0, 2, [0.2f, 0.3f], []);
        Assert.Equal(2, retry.Sequence);
    }

    [Fact]
    public void PublicBatchFactoryAndSnapshotExtractionReturnOwnedValidatedValues()
    {
        float[] input = [0.25f, 0.75f];
        DiarizationFrameBatch batch = DiarizationFrameBatch.FromProbabilities(8, 2, 0.01, input);
        input[0] = 0;
        Assert.Equal(8, batch.FirstFrameIndex);
        Assert.Equal(1, batch.FrameCount);
        Assert.Equal(0.25f, batch.GetProbability(8, 0));
        Assert.Throws<ArgumentException>(() => DiarizationFrameBatch.FromProbabilities(0, 2, 0.01, [0.1f]));
        Assert.Throws<ArgumentException>(() => DiarizationFrameBatch.FromProbabilities(0, 1, 0.01, [2f]));
        Assert.Throws<OverflowException>(() => DiarizationFrameBatch.FromProbabilities(long.MaxValue, 1, 0.01, [0.1f]));

        var snapshot = new DiarizationSnapshot(4, 5, 2, 0.01, [0.6f, 0.7f], [], false);
        DiarizationFrameBatch extracted = snapshot.ToFrameBatch();
        Assert.Equal(4, extracted.FirstFrameIndex);
        Assert.Equal(1, extracted.FrameCount);
        Assert.Equal(0.7f, extracted.GetProbability(4, 1));
    }

    [Fact]
    public void StreamResultReaderUsesScratchSeamAndIndependentUpdateCursor()
    {
        var handle = new StreamHandle(1, new ModelHandle(0));
        float[] current = [0.2f, 0.4f];
        long total = 2;
        long retained = 0;
        var stream = new DiarizationStream(handle, 1, 0.08,
            (_, _, _, _) => NativeStatus.Ok,
            _ => NativeStatus.Ok,
            status => new InvalidOperationException(status.ToString()),
            _ => total,
            _ => retained,
            (_, buffer, capacity) =>
            {
                Assert.Equal(current.Length, (int)capacity);
                Array.Copy(current, buffer, current.Length);
                return NativeStatus.Ok;
            },
            _ => []);

        try
        {
            DiarizationResultUpdate first = stream.ReadResultUpdates();
            Assert.Equal(1, first.Sequence);
            Assert.Equal(2, first.NewFrames.FrameCount);

            DiarizationFrameBatch legacy = stream.ReadNewFrames();
            Assert.Equal(0, legacy.FirstFrameIndex);
            Assert.Equal(2, legacy.FrameCount);

            current = [0.2f, 0.9f];
            DiarizationResultUpdate second = stream.ReadResultUpdates();
            Assert.Equal(2, second.Sequence);
            Assert.Single(second.RevisedFrames);
            Assert.Equal(1, second.RevisedFrames[0].FirstFrameIndex);
            Assert.Equal(0, second.NewFrames.FrameCount);

            total = 4;
            retained = 3;
            current = [0.7f];
            Assert.Throws<DiarizationFrameHistoryGapException>(() => stream.ReadResultUpdates());
            Assert.Equal(2, second.Sequence);
            DiarizationResultUpdate recovered = stream.ReadResultUpdates(allowHistoryGap: true);
            Assert.Equal(new DiarizationFrameRange(2, 3), recovered.HistoryGap);
        }
        finally
        {
            handle.SetHandleAsInvalid();
        }
    }


    [Fact]
    public async Task StreamReadFailuresStayRecoverableAndPushFinishFaultsRejectResultReads()
    {
        int readAttempts = 0;
        var (readStream, readHandle) = CreateStream(
            (_, _, _, _) => NativeStatus.Ok,
            _ => NativeStatus.Ok,
            (_, buffer, _) =>
            {
                buffer[0] = 0.25f;
                return ++readAttempts == 1 ? NativeStatus.Runtime : NativeStatus.Ok;
            });
        try
        {
            Assert.Equal("Fake native read failed: Runtime.",
                Assert.Throws<InvalidOperationException>(() => readStream.ReadResultUpdates()).Message);
            DiarizationResultUpdate retry = readStream.ReadResultUpdates();
            Assert.Equal(1, retry.Sequence);
        }
        finally
        {
            readHandle.SetHandleAsInvalid();
        }

        var (pushStream, pushHandle) = CreateStream(
            (_, _, _, _) => NativeStatus.Runtime,
            _ => NativeStatus.Ok,
            (_, buffer, _) => { buffer[0] = 0.25f; return NativeStatus.Ok; });
        try
        {
            Assert.Throws<InvalidOperationException>(() => pushStream.Push([0.1f], 16000));
            Assert.Throws<InvalidOperationException>(() => pushStream.ReadResultUpdates());
        }
        finally
        {
            pushHandle.SetHandleAsInvalid();
        }

        var (finishStream, finishHandle) = CreateStream(
            (_, _, _, _) => NativeStatus.Ok,
            _ => NativeStatus.Runtime,
            (_, buffer, _) => { buffer[0] = 0.25f; return NativeStatus.Ok; });
        try
        {
            Assert.Throws<InvalidOperationException>(() => finishStream.Finish());
            Assert.Throws<InvalidOperationException>(() => finishStream.ReadResultUpdates());
        }
        finally
        {
            finishHandle.SetHandleAsInvalid();
        }
    }

    [Fact]
    public async Task ResultReadsRejectActiveAsyncOperationsAndResumeAfterReservation()
    {
        var (stream, handle) = CreateStream(
            (_, _, _, _) => NativeStatus.Ok,
            _ => NativeStatus.Ok,
            (_, buffer, _) => { buffer[0] = 0.25f; return NativeStatus.Ok; });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task operation = stream.PushChunksAsync(WaitForReleaseAsync(entered, release), 16000,
                TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Throws<InvalidOperationException>(() => stream.ReadResultUpdates());
            release.SetResult();
            await operation;
            Assert.Equal(1, stream.ReadResultUpdates().Sequence);
        }
        finally
        {
            release.TrySetResult();
            handle.SetHandleAsInvalid();
        }
    }

    private static DiarizationResultUpdate Read(ResultUpdateCursor cursor, long first, long total,
        float[] probabilities, DiarizationSegment[] segments, bool allowHistoryGap = false, bool isFinal = false)
        => cursor.Read(first, total, 1, 0.08, isFinal, allowHistoryGap,
            (buffer, count) =>
            {
                Assert.Equal(count, probabilities.Length);
                Array.Copy(probabilities, buffer, count);
                return NativeStatus.Ok;
            }, () => segments, FakeNativeError);

    private static DiarizationResultUpdate Read(ResultUpdateCursor cursor, long first, long total,
        int speakerCount, float[] probabilities)
        => cursor.Read(first, total, speakerCount, 0.08, false, false,
            (buffer, count) =>
            {
                Assert.Equal(count, probabilities.Length);
                Array.Copy(probabilities, buffer, count);
                return NativeStatus.Ok;
            }, () => [], FakeNativeError);

    private static DiarizationResultUpdate ReadWithSegments(ResultUpdateCursor cursor, long first, long total,
        float[] probabilities, DiarizationSegment[] segments)
        => cursor.Read(first, total, 1, 0.08, false, false,
            (buffer, count) =>
            {
                Array.Copy(probabilities, buffer, count);
                return NativeStatus.Ok;
            }, () => segments, FakeNativeError);

    private static Exception FakeNativeError(NativeStatus status)
        => new InvalidOperationException($"Fake native read failed: {status}.");

    private static (DiarizationStream Stream, StreamHandle Handle) CreateStream(
        Func<StreamHandle, float[], nuint, int, NativeStatus> push,
        Func<StreamHandle, NativeStatus> finish,
        Func<StreamHandle, float[], nuint, NativeStatus> readProbabilities)
    {
        var handle = new StreamHandle(1, new ModelHandle(0));
        var stream = new DiarizationStream(handle, 1, 0.08, push, finish, FakeNativeError,
            _ => 1, _ => 0, readProbabilities, _ => []);
        return (stream, handle);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<float>> WaitForReleaseAsync(
        TaskCompletionSource entered, TaskCompletionSource release)
    {
        entered.SetResult();
        await release.Task;
        yield break;
    }

    private static DiarizationSegment Seg(int speaker) => new(speaker, speaker + 0.5, 1);
}
