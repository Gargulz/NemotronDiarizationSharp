using NemotronDiarizationSharp.Native;
using System.Runtime.CompilerServices;

namespace NemotronDiarizationSharp;

public sealed class DiarizationStream : IDisposable
{
    private readonly StreamHandle _handle;
    private readonly object _sync = new();
    private readonly int _speakerCount;
    private readonly double _secondsPerFrame;
    private readonly Func<StreamHandle, float[], nuint, int, NativeStatus> _pushNative;
    private readonly Func<StreamHandle, NativeStatus> _finishNative;
    private readonly Func<NativeStatus, Exception> _nativeErrorFactory;
    private readonly Func<StreamHandle, long> _frameCountReader;
    private readonly Func<StreamHandle, long> _retainedStartReader;
    private readonly Func<StreamHandle, float[], nuint, NativeStatus> _probabilityReader;
    private readonly Func<StreamHandle, DiarizationSegment[]> _resultSegmentReader;
    private bool _finished;
    private int? _sampleRate;
    private readonly IncrementalFrameCursor _frameCursor = new();
    private readonly ResultUpdateCursor _resultCursor = new();
    private bool _asyncOperationActive;
    private bool _faulted;

    internal DiarizationStream(StreamHandle handle, int speakerCount, double secondsPerFrame)
        : this(handle, speakerCount, secondsPerFrame, NativeMethods.DiarStreamPushF32,
            NativeMethods.DiarStreamFinish, status => NativeCallException.Create(status))
    {
    }

    internal DiarizationStream(StreamHandle handle, int speakerCount, double secondsPerFrame,
        Func<StreamHandle, float[], nuint, int, NativeStatus> pushNative,
        Func<StreamHandle, NativeStatus> finishNative,
        Func<NativeStatus, Exception> nativeErrorFactory)
        : this(handle, speakerCount, secondsPerFrame, pushNative, finishNative, nativeErrorFactory,
            NativeMethods.DiarFrameCount, NativeMethods.DiarFrameProbsStart, NativeMethods.DiarFrameProbs,
            stream => ReadResultSegments(stream, speakerCount, nativeErrorFactory))
    {
    }

    internal DiarizationStream(StreamHandle handle, int speakerCount, double secondsPerFrame,
        Func<StreamHandle, float[], nuint, int, NativeStatus> pushNative,
        Func<StreamHandle, NativeStatus> finishNative,
        Func<NativeStatus, Exception> nativeErrorFactory,
        Func<StreamHandle, long> frameCountReader,
        Func<StreamHandle, long> retainedStartReader,
        Func<StreamHandle, float[], nuint, NativeStatus> probabilityReader,
        Func<StreamHandle, DiarizationSegment[]> resultSegmentReader)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        _speakerCount = speakerCount;
        _secondsPerFrame = secondsPerFrame;
        _pushNative = pushNative ?? throw new ArgumentNullException(nameof(pushNative));
        _finishNative = finishNative ?? throw new ArgumentNullException(nameof(finishNative));
        _nativeErrorFactory = nativeErrorFactory ?? throw new ArgumentNullException(nameof(nativeErrorFactory));
        _frameCountReader = frameCountReader ?? throw new ArgumentNullException(nameof(frameCountReader));
        _retainedStartReader = retainedStartReader ?? throw new ArgumentNullException(nameof(retainedStartReader));
        _probabilityReader = probabilityReader ?? throw new ArgumentNullException(nameof(probabilityReader));
        _resultSegmentReader = resultSegmentReader ?? throw new ArgumentNullException(nameof(resultSegmentReader));
    }

    /// <summary>
    /// Appends mono float32 PCM. Samples are expected to be normalized to [-1, 1]; finite values
    /// are passed unchanged, without clipping or normalization. The explicit sample rate must be
    /// between 8000 and 96000 Hz and must remain constant after the first non-empty push. Empty
    /// pushes do not select a rate; after one is selected, they must specify the same rate. Unlike
    /// the native API, 0 is not a default-rate sentinel.
    /// </summary>
    public void Push(ReadOnlySpan<float> monoPcm, int sampleRate)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            EnsureNoAsyncOperation();
            EnsureNotFaulted();
            if (_finished)
                throw new InvalidOperationException("The stream has already been finished.");
            ValidatePushInput(monoPcm, sampleRate, _sampleRate);
            if (monoPcm.IsEmpty)
                return;

            float[] samples = monoPcm.ToArray();
            InvokeNativeMutation(() => _pushNative(_handle, samples, (nuint)samples.Length, sampleRate));
            _sampleRate ??= sampleRate;
        }
    }

    /// <summary>
    /// Pushes asynchronous PCM chunks in order. Native processing runs synchronously on a worker
    /// thread, one chunk at a time; it is not interruptible once a native push has started.
    /// </summary>
    /// <remarks>
    /// The source receives the cancellation token while being enumerated. Cancellation is observed
    /// between completed pushes, and an already yielded chunk is processed before cancellation is
    /// reported. The stream remains open after completion or cancellation. Await this task before
    /// using or disposing this stream. Do not modify yielded buffers until this task completes.
    /// </remarks>
    public Task PushChunksAsync(IAsyncEnumerable<ReadOnlyMemory<float>> chunks, int sampleRate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            EnsureNoAsyncOperation();
            EnsureNotFaulted();
            if (_finished)
                throw new InvalidOperationException("The stream has already been finished.");
            ValidatePushInput(ReadOnlySpan<float>.Empty, sampleRate, _sampleRate);
            cancellationToken.ThrowIfCancellationRequested();
            // Резервируем поток на всё перечисление, включая ожидание следующего фрагмента.
            _asyncOperationActive = true;
        }

        return PushChunksCoreAsync(chunks, sampleRate, cancellationToken);
    }

    /// <summary>
    /// Processes PCM chunks in order and yields the new or revised results after each chunk,
    /// followed by one final result after the source completes.
    /// </summary>
    /// <remarks>
    /// Processing is pull-based: the next chunk is not requested until the consumer asks for the
    /// next result. The stream is reserved for the lifetime of the enumeration. An acquired chunk
    /// completes processing before cancellation is observed. Early enumeration termination leaves
    /// the stream open and does not call Finish.
    /// </remarks>
    public async IAsyncEnumerable<DiarizationResultUpdate> ProcessStreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<float>> chunks, int sampleRate,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            EnsureNoAsyncOperation();
            EnsureNotFaulted();
            if (_finished)
                throw new InvalidOperationException("The stream has already been finished.");
            ValidatePushInput(ReadOnlySpan<float>.Empty, sampleRate, _sampleRate);
            cancellationToken.ThrowIfCancellationRequested();
            _asyncOperationActive = true;
        }

        IAsyncEnumerator<ReadOnlyMemory<float>>? enumerator = null;
        try
        {
            enumerator = chunks.GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool hasChunk = await enumerator.MoveNextAsync().ConfigureAwait(false);
                if (!hasChunk)
                    break;

                // Once Current has been acquired, finish its synchronous mutation and result read
                // without passing cancellation into scheduling or native processing.
                ReadOnlyMemory<float> chunk = enumerator.Current;
                DiarizationResultUpdate update = await Task.Run(
                    () => ProcessAsyncResultChunk(chunk, sampleRate)).ConfigureAwait(false);
                yield return update;
            }

            IAsyncEnumerator<ReadOnlyMemory<float>> completedEnumerator = enumerator;
            enumerator = null;
            await completedEnumerator.DisposeAsync().ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            DiarizationResultUpdate finalUpdate = await Task.Run(FinishAndReadAsyncResult).ConfigureAwait(false);
            yield return finalUpdate;
        }
        finally
        {
            try
            {
                if (enumerator is not null)
                    await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                    _asyncOperationActive = false;
            }
        }
    }

    private async Task PushChunksCoreAsync(IAsyncEnumerable<ReadOnlyMemory<float>> chunks,
        int sampleRate, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessChunksSequentiallyAsync(chunks, cancellationToken,
                chunk => Task.Run(() => PushAsyncChunk(chunk, sampleRate))).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
                _asyncOperationActive = false;
        }
    }

    internal static async Task ProcessChunksSequentiallyAsync(
        IAsyncEnumerable<ReadOnlyMemory<float>> chunks, CancellationToken cancellationToken,
        Func<ReadOnlyMemory<float>, Task> processChunk)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(processChunk);

        await using IAsyncEnumerator<ReadOnlyMemory<float>> enumerator =
            chunks.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasChunk = await enumerator.MoveNextAsync().ConfigureAwait(false);
            if (!hasChunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            // Уже выданный фрагмент отправляем целиком; отмену проверяем после завершения Push.
            await processChunk(enumerator.Current).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void PushAsyncChunk(ReadOnlyMemory<float> monoPcm, int sampleRate)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            if (!_asyncOperationActive)
                throw new InvalidOperationException("The asynchronous stream operation is no longer active.");
            EnsureNotFaulted();
            if (_finished)
                throw new InvalidOperationException("The stream has already been finished.");

            ReadOnlySpan<float> samples = monoPcm.Span;
            ValidatePushInput(samples, sampleRate, _sampleRate);
            if (samples.IsEmpty)
                return;

            float[] nativeSamples = samples.ToArray();
            InvokeNativeMutation(() => _pushNative(_handle, nativeSamples,
                (nuint)nativeSamples.Length, sampleRate));
            _sampleRate ??= sampleRate;
        }
    }

    private DiarizationResultUpdate ProcessAsyncResultChunk(ReadOnlyMemory<float> monoPcm, int sampleRate)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            if (!_asyncOperationActive)
                throw new InvalidOperationException("The asynchronous stream operation is no longer active.");
            EnsureNotFaulted();
            if (_finished)
                throw new InvalidOperationException("The stream has already been finished.");

            ReadOnlySpan<float> samples = monoPcm.Span;
            ValidatePushInput(samples, sampleRate, _sampleRate);
            if (!samples.IsEmpty)
            {
                float[] nativeSamples = samples.ToArray();
                InvokeNativeMutation(() => _pushNative(_handle, nativeSamples,
                    (nuint)nativeSamples.Length, sampleRate));
                _sampleRate ??= sampleRate;
            }

            return ReadResultUpdatesCore(allowHistoryGap: false);
        }
    }

    private DiarizationResultUpdate FinishAndReadAsyncResult()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            if (!_asyncOperationActive)
                throw new InvalidOperationException("The asynchronous stream operation is no longer active.");
            EnsureNotFaulted();
            if (!_finished)
            {
                InvokeNativeMutation(() => _finishNative(_handle));
                _finished = true;
            }

            return ReadResultUpdatesCore(allowHistoryGap: false);
        }
    }

    private DiarizationResultUpdate ReadResultUpdatesCore(bool allowHistoryGap)
    {
        long frameCount = _frameCountReader(_handle);
        long firstRetainedFrame = _retainedStartReader(_handle);
        return _resultCursor.Read(firstRetainedFrame, frameCount, _speakerCount, _secondsPerFrame,
            _finished,
            allowHistoryGap,
            (buffer, floatCount) => _probabilityReader(_handle, buffer, (nuint)floatCount),
            () => _resultSegmentReader(_handle),
            _nativeErrorFactory);
    }

    private void InvokeNativeMutation(Func<NativeStatus> operation)
    {
        try
        {
            NativeStatus status = operation();
            if (status != NativeStatus.Ok)
                throw _nativeErrorFactory(status);
        }
        catch
        {
            // Push and Finish can fail after partially changing native stream state.
            // Only Dispose is safe after an unsuccessful native mutating call.
            _faulted = true;
            throw;
        }
    }

    private void EnsureNoAsyncOperation()
    {
        if (_asyncOperationActive)
            throw new InvalidOperationException("A chunk-processing operation is active on this stream.");
    }

    private void EnsureNotFaulted()
    {
        if (_faulted)
            throw new InvalidOperationException("A native stream operation failed; dispose this stream.");
    }

    internal static void ValidatePushInput(ReadOnlySpan<float> monoPcm, int sampleRate, int? currentSampleRate)
    {
        if (sampleRate is < 8000 or > 96000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be between 8 kHz and 96 kHz.");
        if (currentSampleRate.HasValue && currentSampleRate.Value != sampleRate)
            throw new ArgumentException("The sample rate cannot change within a stream.", nameof(sampleRate));

        foreach (float sample in monoPcm)
        {
            if (!float.IsFinite(sample))
                throw new ArgumentException("PCM samples must be finite float32 values.", nameof(monoPcm));
        }
    }

    internal static void ValidateOfflineInput(ReadOnlySpan<float> monoPcm, int sampleRate)
    {
        if (sampleRate is < 8000 or > 96000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be between 8 kHz and 96 kHz.");

        foreach (float sample in monoPcm)
        {
            if (!float.IsFinite(sample))
                throw new ArgumentException("PCM samples must be finite float32 values.", nameof(monoPcm));
        }
    }

    public void Finish()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            EnsureNoAsyncOperation();
            EnsureNotFaulted();
            if (_finished)
                return;
            InvokeNativeMutation(() => _finishNative(_handle));
            _finished = true;
        }
    }

    public DiarizationSnapshot GetSnapshot()
        => GetSnapshot(segmentationOptions: null);

    /// <summary>Reads a snapshot, applying segmentation settings to the native result.</summary>
    public DiarizationSnapshot GetSnapshot(DiarizationSegmentationOptions? segmentationOptions)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            EnsureNoAsyncOperation();
            EnsureNotFaulted();
            return GetSnapshotCore(segmentationOptions);
        }
    }

    /// <summary>Reads newly available frame probabilities without advancing snapshot reads.</summary>
    /// <remarks>
    /// Native v0.2.0 copies the complete retained probability window for each read. Read often
    /// enough to avoid native history compaction; returned batches own their managed values.
    /// </remarks>
    public DiarizationFrameBatch ReadNewFrames()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            EnsureNoAsyncOperation();
            EnsureNotFaulted();
            return ReadNewFramesCore(_handle, _speakerCount, _secondsPerFrame, _frameCursor,
                _frameCountReader, _retainedStartReader, _probabilityReader);
        }
    }

    /// <summary>Reads owned new and revised probability batches and the latest segment list.</summary>
    /// <param name="allowHistoryGap">When true, accepts and reports frames lost to native history compaction.</param>
    public DiarizationResultUpdate ReadResultUpdates(bool allowHistoryGap = false)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            EnsureNoAsyncOperation();
            EnsureNotFaulted();

            return ReadResultUpdatesCore(allowHistoryGap);
        }
    }

    private static DiarizationSegment[] ReadResultSegments(StreamHandle handle, int speakerCount,
        Func<NativeStatus, Exception> nativeErrorFactory)
    {
        NativeStatus countStatus = NativeMethods.DiarSegments(handle, 0, null, 0, out nuint segmentCount);
        if (countStatus != NativeStatus.Ok)
            throw nativeErrorFactory(countStatus);
        if (segmentCount > (nuint)Array.MaxLength)
            throw new InvalidOperationException("The native library returned too many segments.");

        var nativeSegments = new NativeSegment[(int)segmentCount];
        if (nativeSegments.Length > 0)
        {
            NativeStatus readStatus = NativeMethods.DiarSegments(handle, 0, nativeSegments,
                (nuint)nativeSegments.Length, out nuint actualCount);
            if (readStatus != NativeStatus.Ok)
                throw nativeErrorFactory(readStatus);
            if (actualCount != (nuint)nativeSegments.Length)
                throw new InvalidOperationException("The native library returned an inconsistent segment count.");
        }

        var segments = new DiarizationSegment[nativeSegments.Length];
        for (int index = 0; index < nativeSegments.Length; index++)
        {
            NativeSegment segment = nativeSegments[index];
            segments[index] = new DiarizationSegment(segment.StartTime, segment.EndTime, segment.Speaker);
        }
        return segments;
    }

    private DiarizationSnapshot GetSnapshotCore(DiarizationSegmentationOptions? segmentationOptions)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        NativeSegmentationConfig? nativeSegmentation = segmentationOptions?.ToNativeConfig();
        return ReadSnapshot(_handle, _speakerCount, _secondsPerFrame, _finished, nativeSegmentation);
    }

    private static DiarizationFrameBatch ReadNewFramesCore(StreamHandle handle, int speakerCount,
        double secondsPerFrame, IncrementalFrameCursor cursor,
        Func<StreamHandle, long> frameCountReader,
        Func<StreamHandle, long> retainedStartReader,
        Func<StreamHandle, float[], nuint, NativeStatus> probabilityReader)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, handle);
        long frameCount = frameCountReader(handle);
        long firstFrame = retainedStartReader(handle);
        if (frameCount < 0 || firstFrame < 0 || firstFrame > frameCount)
            throw new InvalidOperationException("The native library returned invalid frame bounds.");
        if (speakerCount <= 0 || !double.IsFinite(secondsPerFrame) || secondsPerFrame <= 0)
            throw new InvalidOperationException("The stream contains invalid native frame metadata.");

        cursor.ValidateWindow(firstFrame, frameCount);

        long retainedFrames = frameCount - firstFrame;
        if (retainedFrames > Array.MaxLength / speakerCount)
            throw new InvalidOperationException("The retained probability buffer exceeds managed array limits.");
        int floatCount = checked((int)(retainedFrames * speakerCount));
        float[] retainedProbabilities = new float[floatCount];
        if (retainedProbabilities.Length > 0)
        {
            NativeStatus status = probabilityReader(handle, retainedProbabilities,
                (nuint)retainedProbabilities.Length);
            if (status != NativeStatus.Ok)
                throw NativeCallException.Create(status);
        }

        return cursor.CreateBatch(firstFrame, frameCount, speakerCount, secondsPerFrame,
            retainedProbabilities);
    }

    internal static DiarizationSnapshot ReadSnapshot(StreamHandle handle, int speakerCount,
        double secondsPerFrame, bool isFinal, NativeSegmentationConfig? segmentationConfig = null)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, handle);
        long frameCount = NativeMethods.DiarFrameCount(handle);
        long firstFrame = NativeMethods.DiarFrameProbsStart(handle);
        if (frameCount < 0 || firstFrame < 0 || firstFrame > frameCount)
            throw new InvalidOperationException("The native library returned invalid frame bounds.");

        long retainedFrames = frameCount - firstFrame;
        if (retainedFrames > Array.MaxLength / speakerCount)
            throw new InvalidOperationException("The retained probability buffer exceeds managed array limits.");
        int floatCount = checked((int)(retainedFrames * speakerCount));

        float[] probabilities = new float[floatCount];
        if (probabilities.Length > 0)
        {
            NativeStatus probabilityStatus = NativeMethods.DiarFrameProbs(handle, probabilities, (nuint)probabilities.Length);
            if (probabilityStatus != NativeStatus.Ok)
                throw NativeCallException.Create(probabilityStatus);
        }

        NativeStatus countStatus = ReadSegments(handle, segmentationConfig, null, 0, out nuint segmentCount);
        if (countStatus != NativeStatus.Ok)
            throw NativeCallException.Create(countStatus);
        if (segmentCount > (nuint)Array.MaxLength)
            throw new InvalidOperationException("The native library returned too many segments.");

        NativeSegment[] nativeSegments = new NativeSegment[(int)segmentCount];
        if (nativeSegments.Length > 0)
        {
            NativeStatus segmentsStatus = ReadSegments(handle, segmentationConfig, nativeSegments,
                (nuint)nativeSegments.Length, out segmentCount);
            if (segmentsStatus != NativeStatus.Ok)
                throw NativeCallException.Create(segmentsStatus);
            if (segmentCount != (nuint)nativeSegments.Length)
                throw new InvalidOperationException("The native library returned an inconsistent segment count.");
        }

        var segments = new DiarizationSegment[nativeSegments.Length];
        double previousStart = -1;
        for (int index = 0; index < nativeSegments.Length; index++)
        {
            NativeSegment segment = nativeSegments[index];
            if (!double.IsFinite(segment.StartTime) || !double.IsFinite(segment.EndTime)
                || segment.StartTime < 0 || segment.EndTime < segment.StartTime
                || segment.StartTime < previousStart)
                throw new InvalidOperationException("The native library returned invalid or unordered segment timestamps.");
            if (segment.Speaker < 1 || segment.Speaker > speakerCount)
                throw new InvalidOperationException($"The native library returned an invalid 1-based speaker ID: {segment.Speaker}.");

            segments[index] = new DiarizationSegment(segment.StartTime, segment.EndTime, segment.Speaker);
            previousStart = segment.StartTime;
        }

        foreach (float probability in probabilities)
        {
            if (!float.IsFinite(probability) || probability is < 0 or > 1)
                throw new InvalidOperationException("The native library returned a speaker probability outside [0, 1].");
        }

        return new DiarizationSnapshot(firstFrame, frameCount, speakerCount, secondsPerFrame, probabilities, segments, isFinal);
    }

    private static NativeStatus ReadSegments(StreamHandle handle, NativeSegmentationConfig? config,
        NativeSegment[]? segments, nuint capacity, out nuint count)
    {
        if (!config.HasValue)
            return NativeMethods.DiarSegments(handle, 0, segments, capacity, out count);

        NativeSegmentationConfig nativeConfig = config.Value;
        return NativeMethods.DiarSegmentsWithConfig(handle, in nativeConfig, segments, capacity, out count);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            EnsureNoAsyncOperation();
            _handle.Dispose();
        }
    }
}
