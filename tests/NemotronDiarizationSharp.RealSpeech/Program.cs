using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NemotronDiarizationSharp;

return await BenchmarkProgram.RunAsync(args);

internal static class BenchmarkProgram
{
    private const int ChunkSeconds = 1;
    private const string Preset = "v3-streaming";
    private static readonly DiarizationModelOptions ModelOptions = new()
    {
        Preset = Preset,
        SpeakerCacheFrames = 264,
        FifoFrames = 40,
        ChunkFrames = 340,
        RightContextFrames = 40,
        UpdatePeriodFrames = 300
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            if (options.Help)
            {
                Console.WriteLine(Options.Usage);
                return 0;
            }

            var wave = new WavePcmReader(options.AudioPath);
            ValidateExcerpt(options, wave.DurationSeconds);
            List<RttmSegment> reference = RttmFile.Read(options.ReferencePath)
                .Select(x => x.Crop(options.StartSeconds, options.DurationSeconds))
                .Where(x => x is not null)
                .Select(x => x!)
                .ToList();
            if (reference.Count == 0)
                throw new InvalidDataException("No reference RTTM segments intersect the requested excerpt.");

            string[] recordingIds = reference.Select(x => x.Recording).Distinct(StringComparer.Ordinal).ToArray();
            if (recordingIds.Length != 1)
                throw new InvalidDataException("Reference RTTM must contain exactly one recording ID for an excerpt.");
            string recordingId = recordingIds[0];
            int referenceSpeakerCount = reference.Select(x => x.Speaker).Distinct(StringComparer.Ordinal).Count();
            var report = new RunReport
            {
                AudioPath = Path.GetFullPath(options.AudioPath),
                AudioSha256 = HashFile(options.AudioPath),
                ReferencePath = Path.GetFullPath(options.ReferencePath),
                ReferenceSha256 = HashFile(options.ReferencePath),
                ExcerptStartSeconds = options.StartSeconds,
                ExcerptDurationSeconds = options.DurationSeconds,
                SourceSampleRate = wave.SampleRate,
                SourceChannels = wave.Channels,
                ReferenceSegmentCount = reference.Count,
                ReferenceSpeakerCount = referenceSpeakerCount,
                ReferenceOverlapSeconds = MeasureOverlap(reference, options.DurationSeconds),
                Preset = Preset,
                SpeakerCacheFrames = 264,
                FifoFrames = 40,
                ChunkFrames = 340,
                RightContextFrames = 40,
                UpdatePeriodFrames = 300,
                ChunkPushSeconds = ChunkSeconds,
                CollarSeconds = 0,
                OverlapIncluded = true,
                DerStatus = "NOT_MEASURED",
                DerNote = "No trusted scoring executable was available; reference and prediction RTTMs are exported for external scoring."
            };

            if (options.Preflight)
            {
                Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
                return 0;
            }

            ApplyBelowNormalPriority(report);
            string modelPath = Environment.GetEnvironmentVariable("NEMOTONDIARIZATIONSHARP_MODEL")
                ?? Environment.GetEnvironmentVariable("NEMOTONSHARP_MODEL")
                ?? throw new InvalidOperationException("Set NEMOTONDIARIZATIONSHARP_MODEL to the verified model path.");
            string expectedModelHash = "08456d9e22cd9a323c0364d98375f3746d6e68507ebb705cd46438c534c7a3a1";
            report.ModelPath = Path.GetFullPath(modelPath);
            report.ModelSha256 = HashFile(modelPath);
            if (!string.Equals(report.ModelSha256, expectedModelHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Model SHA-256 mismatch: {report.ModelSha256}.");

            using var model = DiarizationModel.Load(modelPath, ModelOptions);
            report.NativeRuntimeVersion = DiarizationModel.NativeRuntimeVersion;
            report.ModelSpeakerChannels = model.SpeakerCount;
            report.SecondsPerFrame = model.SecondsPerFrame;

            var timer = Stopwatch.StartNew();
            using var stream = model.OpenStream();
            long totalFrames = checked((long)Math.Round(options.DurationSeconds * wave.SampleRate, MidpointRounding.AwayFromZero));
            int chunkFrames = checked(ChunkSeconds * wave.SampleRate);
            using var audio = wave.OpenExcerpt(options.StartSeconds, totalFrames);
            var samples = new float[chunkFrames];
            long pushedFrames = 0;
            while (pushedFrames < totalFrames)
            {
                int requested = (int)Math.Min(samples.Length, totalFrames - pushedFrames);
                int read = audio.Read(samples, requested);
                if (read != requested)
                    throw new EndOfStreamException($"Audio ended at excerpt frame {pushedFrames + read} of {totalFrames}.");
                stream.Push(samples.AsSpan(0, read), wave.SampleRate);
                pushedFrames += read;
            }
            stream.Finish();
            timer.Stop();

            DiarizationSnapshot snapshot = stream.GetSnapshot();
            ValidateSnapshot(snapshot, options.DurationSeconds);
            report.ConsistencyChecksPassed = true;
            List<RttmSegment> predicted = snapshot.Segments
                .Select(x => new RttmSegment(recordingId, x.StartTime, x.EndTime - x.StartTime, $"speaker_{x.Speaker}"))
                .Where(x => x.Duration > 0)
                .ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.PredictionPath))!);
            RttmFile.Write(options.PredictionPath, predicted);
            RttmFile.Write(options.CroppedReferencePath, reference);

            report.ProcessingSeconds = timer.Elapsed.TotalSeconds;
            report.RealTimeFactor = report.ProcessingSeconds / options.DurationSeconds;
            report.PredictionPath = Path.GetFullPath(options.PredictionPath);
            report.PredictionSha256 = HashFile(options.PredictionPath);
            report.CroppedReferenceOutputPath = Path.GetFullPath(options.CroppedReferencePath);
            report.CroppedReferenceOutputSha256 = HashFile(options.CroppedReferencePath);
            report.DetectedSpeakerCount = predicted.Select(x => x.Speaker).Distinct(StringComparer.Ordinal).Count();
            report.PredictedSegmentCount = predicted.Count;
            report.FrameCount = snapshot.FrameCount;
            report.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
            report.ObservedActiveOverlapSeconds = MeasureOverlap(predicted, options.DurationSeconds);
            report.SampleCount = totalFrames;

            string json = JsonSerializer.Serialize(report, JsonOptions);
            Console.WriteLine(json);
            if (!string.IsNullOrWhiteSpace(options.ReportPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ReportPath))!);
                await File.WriteAllTextAsync(options.ReportPath, json + Environment.NewLine, Encoding.UTF8);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            Console.Error.WriteLine(Options.Usage);
            return 1;
        }
    }

    private static void ValidateExcerpt(Options options, double audioDuration)
    {
        if (!double.IsFinite(options.StartSeconds) || options.StartSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(options.StartSeconds));
        if (!double.IsFinite(options.DurationSeconds) || options.DurationSeconds is < 180 or > 300)
            throw new ArgumentOutOfRangeException(nameof(options.DurationSeconds), "Pilot excerpts must be 180–300 seconds.");
        if (options.StartSeconds + options.DurationSeconds > audioDuration)
            throw new ArgumentOutOfRangeException(nameof(options.StartSeconds), "Requested excerpt extends beyond the audio file.");
    }

    private static void ValidateSnapshot(DiarizationSnapshot snapshot, double durationSeconds)
    {
        if (!snapshot.IsFinal || snapshot.FrameCount <= 0 || snapshot.FirstProbabilityFrame != 0)
            throw new InvalidDataException("The finished stream did not return a complete final probability snapshot.");
        for (long frame = snapshot.FirstProbabilityFrame; frame < snapshot.FrameCount; frame++)
        {
            double timestamp = snapshot.GetFrameTimestamp(frame);
            if (!double.IsFinite(timestamp) || timestamp > durationSeconds + snapshot.SecondsPerFrame)
                throw new InvalidDataException("Frame timing is invalid or extends beyond the excerpt.");
            for (int speaker = 0; speaker < snapshot.SpeakerCount; speaker++)
            {
                float probability = snapshot.GetProbability(frame, speaker);
                if (!float.IsFinite(probability) || probability is < 0 or > 1)
                    throw new InvalidDataException("The output probability buffer contains an invalid value.");
            }
        }

        double previousStart = 0;
        foreach (DiarizationSegment segment in snapshot.Segments)
        {
            if (!double.IsFinite(segment.StartTime) || !double.IsFinite(segment.EndTime)
                || segment.StartTime < previousStart || segment.StartTime < 0
                || segment.EndTime < segment.StartTime || segment.EndTime > durationSeconds + snapshot.SecondsPerFrame
                || segment.Speaker < 1 || segment.Speaker > snapshot.SpeakerCount)
                throw new InvalidDataException("A predicted segment has invalid timing or speaker index.");
            previousStart = segment.StartTime;
        }
    }

    private static void ApplyBelowNormalPriority(RunReport report)
    {
        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
            report.BelowNormalPriorityApplied = true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            report.BelowNormalPriorityApplied = false;
            report.PriorityNote = exception.Message;
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static double MeasureOverlap(IReadOnlyList<RttmSegment> segments, double duration)
    {
        var points = segments.SelectMany(x => new[]
        {
            (Time: Math.Clamp(x.Start, 0, duration), Delta: 1, Speaker: x.Speaker),
            (Time: Math.Clamp(x.Start + x.Duration, 0, duration), Delta: -1, Speaker: x.Speaker)
        }).OrderBy(x => x.Time).ThenBy(x => x.Delta).ToArray();
        var active = new HashSet<string>(StringComparer.Ordinal);
        double previous = 0;
        double overlap = 0;
        foreach (var point in points)
        {
            if (active.Count > 1)
                overlap += point.Time - previous;
            if (point.Delta < 0)
                active.Remove(point.Speaker);
            else
                active.Add(point.Speaker);
            previous = point.Time;
        }
        return overlap;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class RunReport
    {
        public string AudioPath { get; set; } = "";
        public string AudioSha256 { get; set; } = "";
        public string ReferencePath { get; set; } = "";
        public string ReferenceSha256 { get; set; } = "";
        public double ExcerptStartSeconds { get; set; }
        public double ExcerptDurationSeconds { get; set; }
        public int SourceSampleRate { get; set; }
        public int SourceChannels { get; set; }
        public long SampleCount { get; set; }
        public int ReferenceSegmentCount { get; set; }
        public int ReferenceSpeakerCount { get; set; }
        public double ReferenceOverlapSeconds { get; set; }
        public int ModelSpeakerChannels { get; set; }
        public int DetectedSpeakerCount { get; set; }
        public int PredictedSegmentCount { get; set; }
        public long FrameCount { get; set; }
        public double SecondsPerFrame { get; set; }
        public string Preset { get; set; } = "";
        public int SpeakerCacheFrames { get; set; }
        public int FifoFrames { get; set; }
        public int ChunkFrames { get; set; }
        public int RightContextFrames { get; set; }
        public int UpdatePeriodFrames { get; set; }
        public int ChunkPushSeconds { get; set; }
        public int CollarSeconds { get; set; }
        public bool OverlapIncluded { get; set; }
        public double ObservedActiveOverlapSeconds { get; set; }
        public string? ModelPath { get; set; }
        public string? ModelSha256 { get; set; }
        public string? NativeRuntimeVersion { get; set; }
        public double? ProcessingSeconds { get; set; }
        public double? RealTimeFactor { get; set; }
        public long? PeakWorkingSetBytes { get; set; }
        public bool BelowNormalPriorityApplied { get; set; }
        public bool ConsistencyChecksPassed { get; set; }
        public List<string> SdkCorrectnessIssues { get; set; } = [];
        public string? PriorityNote { get; set; }
        public string DerStatus { get; set; } = "";
        public string DerNote { get; set; } = "";
        public string? PredictionPath { get; set; }
        public string? PredictionSha256 { get; set; }
        public string? CroppedReferenceOutputPath { get; set; }
        public string? CroppedReferenceOutputSha256 { get; set; }
    }

    private sealed record Options(string AudioPath, string ReferencePath, double StartSeconds,
        double DurationSeconds, string PredictionPath, string CroppedReferencePath, string? ReportPath,
        bool Preflight, bool Help)
    {
        public const string Usage = "Usage: RealSpeech --audio <wav> --reference-rttm <rttm> --start <seconds> --duration <180..300> --prediction-rttm <path> --cropped-reference-rttm <path> [--report <json>] [--preflight]";

        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool preflight = false;
            bool help = false;
            for (int index = 0; index < args.Length; index++)
            {
                string key = args[index];
                if (key is "--help" or "-h") { help = true; continue; }
                if (key == "--preflight") { preflight = true; continue; }
                if (!key.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                    throw new ArgumentException($"Invalid or incomplete option: {key}");
                values.Add(key, args[++index]);
            }
            if (help) return new Options("", "", 0, 0, "", "", null, preflight, true);
            string Required(string key) => values.TryGetValue(key, out string? value)
                ? value : throw new ArgumentException($"Required option {key} is missing.");
            double Number(string key) => double.Parse(Required(key), NumberStyles.Float, CultureInfo.InvariantCulture);
            return new Options(Required("--audio"), Required("--reference-rttm"), Number("--start"),
                Number("--duration"), Required("--prediction-rttm"), Required("--cropped-reference-rttm"),
                values.GetValueOrDefault("--report"), preflight, false);
        }
    }
}

internal sealed class WavePcmReader
{
    private readonly string _path;
    private readonly long _dataOffset;
    private readonly long _dataLength;
    private readonly ushort _format;
    private readonly ushort _bitsPerSample;
    private readonly ushort _blockAlign;

    public WavePcmReader(string path)
    {
        _path = Path.GetFullPath(path);
        using var reader = new BinaryReader(File.OpenRead(_path));
        if (ReadTag(reader) != "RIFF") throw new InvalidDataException("Only RIFF/WAVE files are supported.");
        _ = reader.ReadUInt32();
        if (ReadTag(reader) != "WAVE") throw new InvalidDataException("Invalid WAVE file header.");
        ushort? format = null;
        ushort? channels = null;
        int? sampleRate = null;
        ushort? blockAlign = null;
        ushort? bits = null;
        long dataOffset = -1;
        long dataLength = -1;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            string tag = ReadTag(reader);
            uint size = reader.ReadUInt32();
            long chunkEnd = checked(reader.BaseStream.Position + size);
            if (chunkEnd > reader.BaseStream.Length) throw new InvalidDataException("WAVE chunk extends beyond the file.");
            if (tag == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("WAVE format chunk is too short.");
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = checked((int)reader.ReadUInt32());
                _ = reader.ReadUInt32();
                blockAlign = reader.ReadUInt16();
                bits = reader.ReadUInt16();
            }
            else if (tag == "data" && dataOffset < 0)
            {
                dataOffset = reader.BaseStream.Position;
                dataLength = size;
            }
            reader.BaseStream.Position = chunkEnd + (size & 1);
        }
        if (format is null || channels is null || sampleRate is null || blockAlign is null || bits is null || dataOffset < 0)
            throw new InvalidDataException("WAVE format or data chunk is missing.");
        if (format is not 1 and not 3 || channels.Value < 1 || sampleRate.Value < 8000 || sampleRate.Value > 96000
            || bits.Value is not 16 and not 24 and not 32 || (format == 3 && bits != 32))
            throw new InvalidDataException("Supported WAVE input is PCM 16/24/32-bit or IEEE float32, 8–96 kHz.");
        if (blockAlign.Value != channels.Value * (bits.Value / 8) || dataLength % blockAlign.Value != 0)
            throw new InvalidDataException("WAVE block alignment is invalid.");
        _format = format.Value;
        Channels = channels.Value;
        SampleRate = sampleRate.Value;
        _bitsPerSample = bits.Value;
        _blockAlign = blockAlign.Value;
        _dataOffset = dataOffset;
        _dataLength = dataLength;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public long TotalFrames => _dataLength / _blockAlign;
    public double DurationSeconds => (double)TotalFrames / SampleRate;

    public ExcerptReader OpenExcerpt(double startSeconds, long frameCount)
    {
        long startFrame = checked((long)Math.Round(startSeconds * SampleRate, MidpointRounding.AwayFromZero));
        if (startFrame < 0 || frameCount < 0 || startFrame + frameCount > TotalFrames)
            throw new ArgumentOutOfRangeException(nameof(startSeconds));
        var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        stream.Position = checked(_dataOffset + startFrame * _blockAlign);
        return new ExcerptReader(stream, _format, Channels, SampleRate, _bitsPerSample, _blockAlign, frameCount);
    }

    private static string ReadTag(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));

    internal sealed class ExcerptReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly ushort _format;
        private readonly int _channels;
        private readonly int _bits;
        private readonly ushort _blockAlign;
        private long _remainingFrames;
        private readonly byte[] _frame;

        public ExcerptReader(FileStream stream, ushort format, int channels, int sampleRate,
            int bits, ushort blockAlign, long frames)
        {
            _stream = stream;
            _format = format;
            _channels = channels;
            SampleRate = sampleRate;
            _bits = bits;
            _blockAlign = blockAlign;
            _remainingFrames = frames;
            _frame = new byte[blockAlign];
        }

        public int SampleRate { get; }

        public int Read(float[] destination, int count)
        {
            int frames = (int)Math.Min(count, _remainingFrames);
            for (int frameIndex = 0; frameIndex < frames; frameIndex++)
            {
                _stream.ReadExactly(_frame);
                double sum = 0;
                for (int channel = 0; channel < _channels; channel++)
                    sum += Decode(_frame.AsSpan(channel * (_bits / 8), _bits / 8));
                destination[frameIndex] = (float)(sum / _channels);
            }
            _remainingFrames -= frames;
            return frames;
        }

        private double Decode(ReadOnlySpan<byte> sample)
        {
            if (_format == 3)
            {
                float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample));
                if (!float.IsFinite(value)) throw new InvalidDataException("WAVE input contains NaN or infinity.");
                return Math.Clamp(value, -1f, 1f);
            }
            return _bits switch
            {
                16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768d,
                24 => Decode24(sample) / 8388608d,
                32 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648d,
                _ => throw new InvalidDataException("Unsupported PCM sample width.")
            };
        }

        private static int Decode24(ReadOnlySpan<byte> sample)
        {
            int value = sample[0] | sample[1] << 8 | sample[2] << 16;
            return (value & 0x800000) == 0 ? value : value | unchecked((int)0xFF000000);
        }

        public void Dispose() => _stream.Dispose();
    }
}

internal sealed record RttmSegment(string Recording, double Start, double Duration, string Speaker)
{
    public double End => Start + Duration;

    public RttmSegment? Crop(double start, double duration)
    {
        double clippedStart = Math.Max(Start, start);
        double clippedEnd = Math.Min(End, start + duration);
        return clippedEnd <= clippedStart ? null
            : this with { Start = clippedStart - start, Duration = clippedEnd - clippedStart };
    }
}

internal static class RttmFile
{
    public static List<RttmSegment> Read(string path)
    {
        var result = new List<RttmSegment>();
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 8 || fields[0] != "SPEAKER")
                throw new InvalidDataException($"Invalid RTTM line: {line}");
            double start = double.Parse(fields[3], CultureInfo.InvariantCulture);
            double duration = double.Parse(fields[4], CultureInfo.InvariantCulture);
            if (!double.IsFinite(start) || !double.IsFinite(duration) || start < 0 || duration <= 0)
                throw new InvalidDataException($"Invalid RTTM timing: {line}");
            result.Add(new RttmSegment(fields[1], start, duration, fields[7]));
        }
        if (result.Count == 0) throw new InvalidDataException("RTTM file contains no speaker segments.");
        return result;
    }

    public static void Write(string path, IEnumerable<RttmSegment> segments)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (RttmSegment segment in segments.OrderBy(x => x.Start).ThenBy(x => x.Speaker, StringComparer.Ordinal))
            writer.WriteLine(FormattableString.Invariant($"SPEAKER {segment.Recording} 1 {segment.Start:F6} {segment.Duration:F6} <NA> <NA> {segment.Speaker} <NA> <NA>"));
    }
}

