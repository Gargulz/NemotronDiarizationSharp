using NemotronDiarizationSharp.Native;

namespace NemotronDiarizationSharp;

public sealed class DiarizationStream : IDisposable
{
    private readonly StreamHandle _handle;
    private readonly object _sync = new();
    private readonly int _speakerCount;
    private readonly double _secondsPerFrame;
    private bool _finished;
    private int? _sampleRate;
    private readonly IncrementalFrameCursor _frameCursor = new();
    private bool _asyncOperationActive;
    private bool _faulted;

    internal DiarizationStream(StreamHandle handle, int speakerCount, double secondsPerFrame)
    {
        _handle = handle;
        _speakerCount = speakerCount;
        _secondsPerFrame = secondsPerFrame;
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
            NativeStatus status = NativeMethods.DiarStreamPushF32(_handle, samples, (nuint)samples.Length, sampleRate);
            if (status != NativeStatus.Ok)
                throw NativeCallException.Create(status);
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
            NativeStatus status;
            try
            {
                status = NativeMethods.DiarStreamPushF32(_handle, nativeSamples,
                    (nuint)nativeSamples.Length, sampleRate);
            }
            catch
            {
                // Native мог принять часть данных до ошибки, поэтому дальнейшее использование запрещаем.
                _faulted = true;
                throw;
            }

            if (status != NativeStatus.Ok)
            {
                // Состояние native stream после ошибки неизвестно: разрешаем только Dispose.
                _faulted = true;
                throw NativeCallException.Create(status);
            }
            _sampleRate ??= sampleRate;
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
            throw new InvalidOperationException("An asynchronous native push failed; dispose this stream.");
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
            NativeStatus status = NativeMethods.DiarStreamFinish(_handle);
            if (status != NativeStatus.Ok)
                throw NativeCallException.Create(status);
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
            return ReadNewFramesCore(_handle, _speakerCount, _secondsPerFrame, _frameCursor);
        }
    }

    private DiarizationSnapshot GetSnapshotCore(DiarizationSegmentationOptions? segmentationOptions)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        NativeSegmentationConfig? nativeSegmentation = segmentationOptions?.ToNativeConfig();
        return ReadSnapshot(_handle, _speakerCount, _secondsPerFrame, _finished, nativeSegmentation);
    }

    private static DiarizationFrameBatch ReadNewFramesCore(StreamHandle handle, int speakerCount,
        double secondsPerFrame, IncrementalFrameCursor cursor)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, handle);
        long frameCount = NativeMethods.DiarFrameCount(handle);
        long firstFrame = NativeMethods.DiarFrameProbsStart(handle);
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
            NativeStatus status = NativeMethods.DiarFrameProbs(handle, retainedProbabilities,
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
