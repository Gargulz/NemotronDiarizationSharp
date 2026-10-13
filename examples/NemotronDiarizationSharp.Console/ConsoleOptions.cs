using System.Globalization;

namespace NemotronDiarizationSharp.Examples;

internal sealed record ConsoleOptions
{
    public const string Usage = "Usage: NemotronDiarizationSharp.Console --model model.gguf --input audio.wav --output result.rttm\n" +
        "  [--backend cpu|cuda] [--native-dir directory] [--gpu-device index]\n" +
        "  [--segmentation native|managed] [--recording-id token] [--chunk-frames 1..96000]\n" +
        "  --help\nLegacy: NemotronDiarizationSharp.Console <model.gguf> <mono-pcm-f32le.raw> <sample-rate>";

    public string ModelPath { get; init; } = "";
    public string InputPath { get; init; } = "";
    public string? OutputPath { get; init; }
    public string Backend { get; init; } = "cpu";
    public string Segmentation { get; init; } = "native";
    public string? NativeDirectory { get; init; }
    public int? GpuDevice { get; init; }
    public int? ChunkFrames { get; init; }
    public string RecordingId { get; init; } = "";
    public int? RawSampleRate { get; init; }
    public bool Help { get; init; }

    public static ConsoleOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 1 && args[0] == "--help") return new() { Help = true };
        if (args.Length == 3 && args.All(arg => !arg.StartsWith("--", StringComparison.Ordinal)))
        {
            int rate = ParseInteger(args[2], "Sample rate", 8000, 96000);
            return new() { ModelPath = Required(args[0], "Model"), InputPath = Required(args[1], "Input"), RawSampleRate = rate };
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] names = ["--model", "--input", "--output", "--backend", "--native-dir", "--gpu-device", "--segmentation", "--recording-id", "--chunk-frames"];
        for (int index = 0; index < args.Length; index++)
        {
            string name = args[index];
            if (!names.Contains(name, StringComparer.Ordinal)) throw new ArgumentException($"Unknown option: {name}.");
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {name}.");
            if (!values.TryAdd(name, Required(args[++index], name))) throw new ArgumentException($"Duplicate option: {name}.");
        }
        string model = GetRequired(values, "--model");
        string input = GetRequired(values, "--input");
        string output = GetRequired(values, "--output");
        string backend = values.GetValueOrDefault("--backend", "cpu");
        string segmentation = values.GetValueOrDefault("--segmentation", "native");
        if (backend is not "cpu" and not "cuda") throw new ArgumentException("Backend must be cpu or cuda.");
        if (segmentation is not "native" and not "managed") throw new ArgumentException("Segmentation must be native or managed.");
        int? gpu = values.TryGetValue("--gpu-device", out string? device) ? ParseInteger(device, "GPU device", 0, int.MaxValue) : null;
        if (backend == "cpu" && gpu.HasValue) throw new ArgumentException("--gpu-device requires --backend cuda.");
        if (backend == "cuda") gpu ??= 0;
        int? chunk = values.TryGetValue("--chunk-frames", out string? frames) ? ParseInteger(frames, "Chunk frames", 1, 96000) : null;
        string id = values.GetValueOrDefault("--recording-id") ?? DefaultRecordingId(input);
        ValidateRecordingId(id);
        return new()
        {
            ModelPath = model, InputPath = input, OutputPath = output, Backend = backend,
            Segmentation = segmentation, NativeDirectory = values.GetValueOrDefault("--native-dir"),
            GpuDevice = gpu, ChunkFrames = chunk, RecordingId = id
        };
    }

    public static void ValidateRecordingId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new ArgumentException("Recording ID must be a nonempty token without whitespace or control characters.");
    }

    private static string DefaultRecordingId(string input)
    {
        string name = Path.GetFileNameWithoutExtension(input);
        string id = new(name.Select(character => char.IsWhiteSpace(character) || char.IsControl(character) ? '_' : character).ToArray());
        return string.IsNullOrEmpty(id) ? "recording" : id;
    }

    private static string GetRequired(Dictionary<string, string> values, string name)
        => values.TryGetValue(name, out string? value) ? value : throw new ArgumentException($"Required option: {name}.");

    private static string Required(string value, string name)
        => !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"{name} cannot be empty.");

    private static int ParseInteger(string value, string name, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < minimum || parsed > maximum)
            throw new ArgumentException($"{name} must be an integer in {minimum}..{maximum}.");
        return parsed;
    }
}
