using System.Globalization;
using NemotronDiarizationSharp.Examples;
using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class ConsoleWorkflowTests
{
    private static readonly string[] RequiredArgs = ["--model", "model.gguf", "--input", "audio.wav", "--output", "output.rttm"];

    [Fact]
    public void DefaultsToCpuNativeAndOneSecondChunks()
    {
        ConsoleOptions options = ConsoleOptions.Parse(RequiredArgs);
        Assert.Equal("cpu", options.Backend);
        Assert.Equal("native", options.Segmentation);
        Assert.Null(options.GpuDevice);
        Assert.Null(options.ChunkFrames);
        Assert.Equal("audio", options.RecordingId);
    }

    [Fact]
    public void ParsesAllExplicitOptions()
    {
        ConsoleOptions options = ConsoleOptions.Parse([.. RequiredArgs, "--backend", "cuda", "--gpu-device", "3",
            "--native-dir", "native", "--segmentation", "managed", "--recording-id", "meeting", "--chunk-frames", "96000"]);
        Assert.Equal("cuda", options.Backend);
        Assert.Equal(3, options.GpuDevice);
        Assert.Equal("native", options.NativeDirectory);
        Assert.Equal("managed", options.Segmentation);
        Assert.Equal("meeting", options.RecordingId);
        Assert.Equal(96000, options.ChunkFrames);
    }

    [Fact]
    public void CudaDefaultsToDeviceZero()
    {
        Assert.Equal(0, ConsoleOptions.Parse([.. RequiredArgs, "--backend", "cuda"]).GpuDevice);
    }

    [Theory]
    [InlineData("--backend", "other")]
    [InlineData("--backend", "CPU")]
    [InlineData("--segmentation", "other")]
    [InlineData("--gpu-device", "0")]
    [InlineData("--chunk-frames", "0")]
    [InlineData("--chunk-frames", "96001")]
    [InlineData("--chunk-frames", "-1")]
    [InlineData("--chunk-frames", "1.5")]
    [InlineData("--chunk-frames", "2147483648")]
    [InlineData("--recording-id", "has space")]
    [InlineData("--recording-id", "has\tcontrol")]
    [InlineData("--recording-id", "has\0control")]
    [InlineData("--native-dir", " ")]
    public void RejectsInvalidNamedValues(string name, string value)
        => Assert.Throws<ArgumentException>(() => ConsoleOptions.Parse([.. RequiredArgs, name, value]));

    [Theory]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("abc")]
    [InlineData("+1")]
    public void RejectsInvalidCudaDevice(string value)
        => Assert.Throws<ArgumentException>(() => ConsoleOptions.Parse([.. RequiredArgs, "--backend", "cuda", "--gpu-device", value]));

    [Theory]
    [InlineData("--model")]
    [InlineData("--input")]
    [InlineData("--output")]
    public void RejectsMissingRequiredOption(string name)
    {
        string[] args = RequiredArgs.Where((_, index) => RequiredArgs[index - index % 2] != name).ToArray();
        Assert.Throws<ArgumentException>(() => ConsoleOptions.Parse(args));
    }

    [Fact]
    public void RejectsUnknownDuplicateAndValuelessOptions()
    {
        Assert.Throws<ArgumentException>(() => ConsoleOptions.Parse([]));
        Assert.Throws<ArgumentException>(() => ConsoleOptions.Parse([.. RequiredArgs, "--other", "value"]));
        Assert.Throws<ArgumentException>(() => ConsoleOptions.Parse([.. RequiredArgs, "--model", "second"]));
        Assert.Throws<ArgumentException>(() => ConsoleOptions.Parse([.. RequiredArgs, "--backend"]));
        Assert.Throws<ArgumentException>(() => ConsoleOptions.Parse([.. RequiredArgs, "--backend", "--segmentation", "native"]));
        Assert.Throws<ArgumentException>(() => ConsoleOptions.Parse([.. RequiredArgs, "--help"]));
    }

    [Fact]
    public void NormalizesFilenameRecordingId()
    {
        ConsoleOptions options = ConsoleOptions.Parse(["--model", "model", "--input", "a meeting.wav", "--output", "output"]);
        Assert.Equal("a_meeting", options.RecordingId);
    }

    [Theory]
    [InlineData("8000")]
    [InlineData("96000")]
    public void ParsesLegacyInvocation(string rate)
    {
        ConsoleOptions options = ConsoleOptions.Parse(["model", "raw", rate]);
        Assert.Equal(int.Parse(rate, CultureInfo.InvariantCulture), options.RawSampleRate);
        Assert.Null(options.OutputPath);
        Assert.Equal("cpu", options.Backend);
    }

    [Theory]
    [InlineData("7999")]
    [InlineData("96001")]
    [InlineData("-1")]
    [InlineData("invalid")]
    public void RejectsInvalidLegacyRate(string rate)
        => Assert.Throws<ArgumentException>(() => ConsoleOptions.Parse(["model", "raw", rate]));

    [Fact]
    public void HelpNeedsNoFilesOrInference()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = ConsoleApplication.Run(["--help"], output, error, (_, _, _) => throw new Exception("Inference must not run."));
        Assert.Equal(0, code);
        Assert.Contains("--backend cpu|cuda", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("missing-model")]
    [InlineData("empty-model")]
    [InlineData("missing-input")]
    [InlineData("invalid-wav")]
    [InlineData("nonfinite-wav")]
    [InlineData("missing-output-directory")]
    [InlineData("output-directory")]
    [InlineData("missing-native-directory")]
    public void PreflightFailsBeforeInferenceAndPreservesOutput(string defect)
    {
        using var files = new Fixture();
        string[] args = files.Args();
        switch (defect)
        {
            case "missing-model": File.Delete(files.Model); break;
            case "empty-model": File.WriteAllBytes(files.Model, []); break;
            case "missing-input": File.Delete(files.Input); break;
            case "invalid-wav": File.WriteAllBytes(files.Input, new byte[1]); break;
            case "nonfinite-wav": File.WriteAllBytes(files.Input, Wave(0, float.NaN)); break;
            case "missing-output-directory": args[5] = Path.Combine(files.Directory, "missing", "out.rttm"); break;
            case "output-directory": args[5] = files.Directory; break;
            case "missing-native-directory": args = [.. args, "--native-dir", Path.Combine(files.Directory, "missing")]; break;
        }
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = ConsoleApplication.Run(args, output, error, (_, _, _) => throw new Exception("Inference must not run."));
        Assert.Equal(2, code);
        Assert.StartsWith("Invalid input:", error.ToString());
        Assert.Equal("existing output", File.ReadAllText(files.Output));
        Assert.Single(System.IO.Directory.GetFiles(files.Directory, "*.rttm"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsOutputAliasingInputOrModel(bool model)
    {
        using var files = new Fixture();
        string protectedPath = model ? files.Model : files.Input;
        byte[] before = File.ReadAllBytes(protectedPath);
        string[] args = files.Args();
        args[5] = Path.Combine(files.Directory, ".", Path.GetFileName(protectedPath));
        Assert.Equal(2, ConsoleApplication.Run(args, new StringWriter(), new StringWriter(), (_, _, _) => throw new Exception()));
        Assert.Equal(before, File.ReadAllBytes(protectedPath));
    }

    [Fact]
    public void PathEqualityUsesPlatformCaseSemantics()
    {
        using var files = new Fixture();
        Assert.True(ConsoleApplication.PathsEqual(files.Input, Path.Combine(files.Directory, ".", "a meeting.wav")));
        Assert.Equal(OperatingSystem.IsWindows(), ConsoleApplication.PathsEqual(files.Input, files.Input.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("model-file")]
    [InlineData("input-file")]
    [InlineData("output-file")]
    [InlineData("model-ancestor")]
    [InlineData("input-ancestor")]
    [InlineData("output-ancestor")]
    public void PreflightRejectsReparsePointFilesAndAncestors(string location)
    {
        using var files = new Fixture();
        string selected = location.StartsWith("model", StringComparison.Ordinal) ? files.Model
            : location.StartsWith("input", StringComparison.Ordinal) ? files.Input : files.Output;
        string subdirectory = Path.Combine(files.Directory, "source-or-output");
        System.IO.Directory.CreateDirectory(subdirectory);
        string copy = Path.Combine(subdirectory, Path.GetFileName(selected));
        File.Copy(selected, copy);
        string[] args = files.Args();
        args[location.StartsWith("model", StringComparison.Ordinal) ? 1 : location.StartsWith("input", StringComparison.Ordinal) ? 3 : 5] = copy;
        string linked = location.EndsWith("ancestor", StringComparison.Ordinal) ? subdirectory : copy;
        var inspected = new List<string>();
        byte[] modelBefore = File.ReadAllBytes(files.Model);
        byte[] inputBefore = File.ReadAllBytes(files.Input);
        byte[] copyBefore = File.ReadAllBytes(copy);
        bool inferenceCalled = false;
        using var error = new StringWriter();
        try
        {
            int code = ConsoleApplication.Run(args, new StringWriter(), error, (_, _, _) => { inferenceCalled = true; return []; }, path =>
            {
                inspected.Add(path);
                return ConsoleApplication.PathsEqual(path, linked) ? FileAttributes.ReparsePoint : File.GetAttributes(path);
            });
            Assert.Equal(2, code);
            Assert.False(inferenceCalled);
            Assert.Contains("reparse points", error.ToString());
            Assert.Contains(linked, inspected);
            Assert.Equal(modelBefore, File.ReadAllBytes(files.Model));
            Assert.Equal(inputBefore, File.ReadAllBytes(files.Input));
            Assert.Equal(copyBefore, File.ReadAllBytes(copy));
            Assert.Equal("existing output", File.ReadAllText(files.Output));
            Assert.Empty(System.IO.Directory.GetFiles(files.Directory, "*.tmp"));
            Assert.Empty(System.IO.Directory.GetFiles(subdirectory, "*.tmp"));
        }
        finally { File.Delete(copy); System.IO.Directory.Delete(subdirectory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreflightRejectsLinkedInputOrModelWhenOutputIsItsRealTarget(bool model)
    {
        using var files = new Fixture();
        string linked = model ? files.Model : files.Input;
        byte[] targetBefore = File.ReadAllBytes(files.Output);
        byte[] modelBefore = File.ReadAllBytes(files.Model);
        byte[] inputBefore = File.ReadAllBytes(files.Input);
        bool inferenceCalled = false;
        using var error = new StringWriter();
        int code = ConsoleApplication.Run(files.Args(), new StringWriter(), error, (_, _, _) => { inferenceCalled = true; return []; }, path =>
            ConsoleApplication.PathsEqual(path, linked) ? FileAttributes.ReparsePoint : File.GetAttributes(path));
        Assert.Equal(2, code);
        Assert.False(inferenceCalled);
        Assert.Contains("reparse points", error.ToString());
        Assert.Equal(targetBefore, File.ReadAllBytes(files.Output));
        Assert.Equal(modelBefore, File.ReadAllBytes(files.Model));
        Assert.Equal(inputBefore, File.ReadAllBytes(files.Input));
    }

    [Fact]
    public void InferenceReceivesValidatedOptionsAndBoundedReadableAudio()
    {
        using var files = new Fixture();
        File.WriteAllBytes(files.Input, Wave(0, 0.25f, 0.5f, -0.5f, 2));
        bool invoked = false;
        int code = ConsoleApplication.Run([.. files.Args(), "--backend", "cuda", "--segmentation", "managed", "--chunk-frames", "2"],
            new StringWriter(), new StringWriter(), (options, rate, read) =>
            {
                invoked = true;
                Assert.Equal(Path.GetFullPath(files.Model), options.ModelPath);
                Assert.Equal(0, options.GpuDevice);
                Assert.Equal("managed", options.Segmentation);
                Assert.Equal(2, options.ChunkFrames);
                Assert.Equal(16000, rate);
                float[] buffer = new float[2];
                var samples = new List<float>();
                var counts = new List<int>();
                int count;
                while ((count = read(buffer)) != 0) { counts.Add(count); samples.AddRange(buffer.Take(count)); }
                Assert.Equal(new[] { 2, 2, 1 }, counts);
                Assert.Equal(new[] { 0, 0.25f, 0.5f, -0.5f, 1 }, samples);
                return [new(0.25, 1.25, 1), new(0.5, 1.5, 2)];
            });
        Assert.Equal(0, code);
        Assert.True(invoked);
        string text = File.ReadAllText(files.Output);
        Assert.Contains("SPEAKER a_meeting 1 0.25 1 <NA> <NA> speaker_1", text);
        Assert.Contains("SPEAKER a_meeting 1 0.5 1 <NA> <NA> speaker_2", text);
        Assert.DoesNotContain("existing output", text);
        Assert.Empty(System.IO.Directory.GetFiles(files.Directory, "*.tmp"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("image")]
    [InlineData("entry")]
    [InlineData("version")]
    [InlineData("model")]
    [InlineData("io")]
    [InlineData("decode")]
    public void MapsProcessingErrorsAndPreservesOutput(string kind)
    {
        using var files = new Fixture();
        Exception exception = kind switch
        {
            "missing" => new DllNotFoundException("missing dependency"),
            "image" => new BadImageFormatException("wrong architecture"),
            "entry" => new EntryPointNotFoundException("missing entry point"),
            "version" => new InvalidOperationException("Incompatible native runtime version"),
            "model" => new InvalidOperationException("model load failed"),
            "io" => new IOException("audio read failed"),
            _ => new InvalidDataException("late decode failed")
        };
        using var error = new StringWriter();
        int code = ConsoleApplication.Run(files.Args(), new StringWriter(), error, (_, _, _) => throw exception);
        Assert.Equal(1, code);
        Assert.Contains(exception.Message, error.ToString());
        Assert.Contains("no fallback", error.ToString());
        if (kind is "image" or "entry" or "version") Assert.Contains("Native runtime incompatible", error.ToString());
        if (kind == "missing") Assert.Contains("Native runtime or dependency missing", error.ToString());
        Assert.Equal("existing output", File.ReadAllText(files.Output));
        Assert.Empty(System.IO.Directory.GetFiles(files.Directory, "*.tmp"));
    }

    [Fact]
    public void InvalidSegmentsDoNotReplaceExistingOutputOrLeaveTemporaryFile()
    {
        using var files = new Fixture();
        int code = ConsoleApplication.Run(files.Args(), new StringWriter(), new StringWriter(), (_, _, _) => [new(0, 1, 1), new(1, 0, 2)]);
        Assert.Equal(1, code);
        Assert.Equal("existing output", File.ReadAllText(files.Output));
        Assert.Empty(System.IO.Directory.GetFiles(files.Directory, "*.tmp"));
    }

    [Fact]
    public void OutputIsInvariantAndRetainsOverlapsAndAbsoluteTime()
    {
        using var files = new Fixture();
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            ConsoleApplication.WriteOutput(files.Output, "session", [new(10.25, 12.75, 1), new(11.5, 13, 2)]);
        }
        finally { CultureInfo.CurrentCulture = saved; }
        Assert.Equal("SPEAKER session 1 10.25 2.5 <NA> <NA> speaker_1 <NA> <NA>" + Environment.NewLine
            + "SPEAKER session 1 11.5 1.5 <NA> <NA> speaker_2 <NA> <NA>" + Environment.NewLine, File.ReadAllText(files.Output));
    }

    [Fact]
    public void EmptySegmentsCreateEmptyRttm()
    {
        using var files = new Fixture();
        Assert.Equal(0, ConsoleApplication.Run(files.Args(), new StringWriter(), new StringWriter(), (_, _, _) => []));
        Assert.Equal(0, new FileInfo(files.Output).Length);
    }

    [Fact]
    public void LegacyRawReadsBoundedLittleEndianFloatsAndPrintsSegments()
    {
        using var files = new Fixture();
        File.WriteAllBytes(files.Input, new[] { -2f, 0.5f, 2f }.SelectMany(BitConverter.GetBytes).ToArray());
        using var output = new StringWriter();
        int code = ConsoleApplication.Run([files.Model, files.Input, "16000"], output, new StringWriter(), (options, rate, read) =>
        {
            Assert.Null(options.OutputPath);
            Assert.Equal(16000, rate);
            float[] buffer = new float[2];
            Assert.Equal(2, read(buffer));
            Assert.Equal(new[] { -2f, 0.5f }, buffer);
            Assert.Equal(1, read(buffer));
            Assert.Equal(2f, buffer[0]);
            Assert.Equal(0, read(buffer));
            return [new(0, 1.5, 1)];
        });
        Assert.Equal(0, code);
        Assert.Contains("speaker 1: 0.00s - 1.50s", output.ToString());
        Assert.Equal("existing output", File.ReadAllText(files.Output));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("partial")]
    [InlineData("nonfinite")]
    public void LegacyRawRejectsBadAudioBeforeInference(string kind)
    {
        using var files = new Fixture();
        File.WriteAllBytes(files.Input, kind == "empty" ? [] : kind == "partial" ? new byte[3] : BitConverter.GetBytes(float.NaN));
        Assert.Equal(2, ConsoleApplication.Run([files.Model, files.Input, "16000"], new StringWriter(), new StringWriter(), (_, _, _) => throw new Exception()));
    }

    [Fact]
    public void FullManagedHistorySegmentsEverySpeakerWithAbsoluteTiming()
    {
        var history = new DiarizationProbabilityHistory(null);
        history.Append(DiarizationFrameBatch.FromProbabilities(0, 2, 0.1, new float[] { 0, 0, 1, 0, 1, 1 }));
        history.Append(DiarizationFrameBatch.FromProbabilities(3, 2, 0.1, new float[] { 0, 1, 0, 0 }));
        IReadOnlyList<DiarizationSegment> segments = ConsoleApplication.SegmentHistory(history, 5);
        Assert.Contains(segments, segment => segment.Speaker == 1 && segment.StartTime <= 0.1 && segment.EndTime >= 0.3);
        Assert.Contains(segments, segment => segment.Speaker == 2 && segment.StartTime <= 0.2 && segment.EndTime >= 0.4);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(0, 3)]
    public void ManagedHistoryRejectsMissingInitialOrFinalCoverage(int start, int end)
    {
        var history = new DiarizationProbabilityHistory(null);
        history.Append(DiarizationFrameBatch.FromProbabilities(start, 1, 0.1, new float[] { 1, 1 }));
        Assert.Throws<InvalidOperationException>(() => ConsoleApplication.SegmentHistory(history, end + 1));
    }

    private static byte[] Wave(params float[] samples)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write((uint)(36 + samples.Length * 4)); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16u); writer.Write((ushort)3); writer.Write((ushort)1);
        writer.Write(16000u); writer.Write(64000u); writer.Write((ushort)4); writer.Write((ushort)32);
        writer.Write("data"u8); writer.Write((uint)(samples.Length * 4));
        foreach (float sample in samples) writer.Write(sample);
        return stream.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"nds-console-{Guid.NewGuid():N}");
        public string Model => Path.Combine(Directory, "model.gguf");
        public string Input => Path.Combine(Directory, "a meeting.wav");
        public string Output => Path.Combine(Directory, "out.rttm");

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(Model, new byte[] { 1 });
            File.WriteAllBytes(Input, Wave(0));
            File.WriteAllText(Output, "existing output");
        }

        public string[] Args() => ["--model", Model, "--input", Input, "--output", Output];

        public void Dispose()
        {
            foreach (string file in System.IO.Directory.GetFiles(Directory)) File.Delete(file);
            System.IO.Directory.Delete(Directory);
        }
    }
}
