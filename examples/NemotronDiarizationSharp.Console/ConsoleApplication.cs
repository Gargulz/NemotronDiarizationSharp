using System.Buffers.Binary;
using System.Text;

namespace NemotronDiarizationSharp.Examples;

internal static class ConsoleApplication
{
    internal delegate IReadOnlyList<DiarizationSegment> Inference(ConsoleOptions options, int sampleRate, Func<float[], int> read);

    public static int Run(string[] args, TextWriter output, TextWriter error, Inference? inference = null,
        Func<string, FileAttributes?>? getAttributes = null)
    {
        ConsoleOptions options;
        try
        {
            options = ConsoleOptions.Parse(args);
            if (options.Help) { output.WriteLine(ConsoleOptions.Usage); return 0; }
            options = Preflight(options, getAttributes);
        }
        catch (Exception exception) when (IsInputError(exception))
        {
            error.WriteLine($"Invalid input: {exception.Message}");
            error.WriteLine(ConsoleOptions.Usage);
            return 2;
        }

        try
        {
            using var source = new AudioSource(options);
            IReadOnlyList<DiarizationSegment> segments = (inference ?? Infer)(options, source.SampleRate, source.Read);
            if (options.OutputPath is not null)
            {
                WriteOutput(options.OutputPath, options.RecordingId, segments);
                output.WriteLine($"Wrote {segments.Count} segments to {options.OutputPath}.");
            }
            else
            {
                foreach (DiarizationSegment segment in segments)
                    output.WriteLine(FormattableString.Invariant($"speaker {segment.Speaker}: {segment.StartTime:F2}s - {segment.EndTime:F2}s"));
            }
            return 0;
        }
        catch (Exception exception) when (IsProcessingError(exception))
        {
            string category = exception is DllNotFoundException ? "Native runtime or dependency missing"
                : exception is BadImageFormatException or EntryPointNotFoundException
                    || exception is InvalidOperationException && exception.Message.Contains("incompatible", StringComparison.OrdinalIgnoreCase)
                    ? "Native runtime incompatible"
                : "Processing failed";
            error.WriteLine($"{category}: {exception.Message}");
            error.WriteLine("Check the model and compatible native runtime dependencies. Backend selection is explicit; no fallback is performed.");
            return 1;
        }
    }

    internal static ConsoleOptions Preflight(ConsoleOptions options, Func<string, FileAttributes?>? getAttributes = null)
    {
        string model = Path.GetFullPath(options.ModelPath);
        string input = Path.GetFullPath(options.InputPath);
        string? destination = options.OutputPath is null ? null : Path.GetFullPath(options.OutputPath);
        if (destination is not null && (PathsEqual(destination, input) || PathsEqual(destination, model)))
            throw new ArgumentException("Output must differ from the input audio and model paths.");
        if (!File.Exists(model)) throw new FileNotFoundException("Model file was not found.", model);
        RejectPathLinks(model, getAttributes);
        if (new FileInfo(model).Length == 0) throw new InvalidDataException("Model file is empty.");
        if (!File.Exists(input)) throw new FileNotFoundException("Input audio file was not found.", input);
        RejectPathLinks(input, getAttributes);
        if (destination is not null)
        {
            if (Directory.Exists(destination)) throw new ArgumentException("Output path names a directory.");
            if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new DirectoryNotFoundException("Output directory was not found.");
            RejectPathLinks(destination, getAttributes);
            ConsoleOptions.ValidateRecordingId(options.RecordingId);
        }
        string? nativeDirectory = options.NativeDirectory is null ? null : Path.GetFullPath(options.NativeDirectory);
        if (nativeDirectory is not null && !Directory.Exists(nativeDirectory))
            throw new DirectoryNotFoundException("Native runtime directory was not found.");
        options = options with { ModelPath = model, InputPath = input, OutputPath = destination, NativeDirectory = nativeDirectory };

        // Validate every sample with bounded storage before loading native code. The processing pass reopens the source.
        using var source = new AudioSource(options);
        float[] scratch = new float[options.ChunkFrames ?? source.SampleRate];
        while (source.Read(scratch) != 0) { }
        return options;
    }

    internal static bool PathsEqual(string first, string second)
        => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void RejectPathLinks(string path, Func<string, FileAttributes?>? getAttributes)
    {
        getAttributes ??= ReadAttributes;
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileAttributes? attributes = getAttributes(current);
            if (attributes.HasValue && (attributes.Value & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Model, input and output paths and their ancestry must not contain symbolic links, junctions or reparse points.");
        }
    }

    private static FileAttributes? ReadAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static IReadOnlyList<DiarizationSegment> Infer(ConsoleOptions options, int sampleRate, Func<float[], int> read)
    {
        if (options.NativeDirectory is not null)
            Environment.SetEnvironmentVariable("NEMOTONDIARIZATIONSHARP_NATIVE_DIR", options.NativeDirectory);
        using var model = DiarizationModel.Load(options.ModelPath, new DiarizationModelOptions { GpuDeviceIndex = options.GpuDevice });
        using var stream = model.OpenStream();
        DiarizationProbabilityHistory? history = options.Segmentation == "managed" ? new(maxRetainedFrames: null) : null;
        float[] buffer = new float[options.ChunkFrames ?? sampleRate];
        int count;
        while ((count = read(buffer)) != 0)
        {
            stream.Push(buffer.AsSpan(0, count), sampleRate);
            history?.Apply(stream.ReadResultUpdates());
        }
        stream.Finish();
        if (history is null) return stream.GetSnapshot().Segments;
        DiarizationResultUpdate final = stream.ReadResultUpdates();
        history.Apply(final);
        return SegmentHistory(history, final.FrameCount);
    }

    internal static IReadOnlyList<DiarizationSegment> SegmentHistory(DiarizationProbabilityHistory history, long finalFrameCount)
    {
        if (!history.IsCompleteFromFrameZero || history.EndFrameExclusive != finalFrameCount)
            throw new InvalidOperationException("Managed probability history does not cover every final frame from zero.");
        return DiarizationPostprocessor.Segment(history.GetFrames());
    }

    internal static void WriteOutput(string path, string recordingId, IReadOnlyList<DiarizationSegment> segments)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                DiarizationPostprocessor.WriteRttm(writer, segments, recordingId);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool IsInputError(Exception exception)
        => exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException;

    private static bool IsProcessingError(Exception exception)
        => IsInputError(exception) || exception is InvalidOperationException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException;

    private sealed class AudioSource : IDisposable
    {
        private readonly DiarizationWaveReader? _wave;
        private readonly FileStream? _raw;
        private readonly byte[]? _rawBuffer;
        private long _remaining;

        public AudioSource(ConsoleOptions options)
        {
            if (options.RawSampleRate is null)
            {
                _wave = new DiarizationWaveReader(options.InputPath);
                SampleRate = _wave.SampleRate;
            }
            else
            {
                SampleRate = options.RawSampleRate.Value;
                _raw = File.OpenRead(options.InputPath);
                if (_raw.Length == 0 || _raw.Length % sizeof(float) != 0)
                {
                    _raw.Dispose();
                    throw new InvalidDataException("Raw PCM must be nonempty and its length divisible by four bytes.");
                }
                _remaining = _raw.Length / sizeof(float);
                _rawBuffer = new byte[checked((options.ChunkFrames ?? SampleRate) * sizeof(float))];
            }
        }

        public int SampleRate { get; }

        public int Read(float[] buffer)
        {
            if (_wave is not null) return _wave.Read(buffer);
            int count = (int)Math.Min(Math.Min(buffer.Length, _rawBuffer!.Length / sizeof(float)), _remaining);
            try { _raw!.ReadExactly(_rawBuffer.AsSpan(0, count * sizeof(float))); }
            catch (EndOfStreamException exception) { throw new InvalidDataException("Raw PCM input is truncated.", exception); }
            for (int index = 0; index < count; index++)
            {
                float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_rawBuffer.AsSpan(index * sizeof(float))));
                if (!float.IsFinite(value)) throw new InvalidDataException("Raw PCM contains NaN or infinity.");
                buffer[index] = value;
            }
            _remaining -= count;
            return count;
        }

        public void Dispose() { _wave?.Dispose(); _raw?.Dispose(); }
    }
}
