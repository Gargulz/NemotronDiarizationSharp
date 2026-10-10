using NemotronDiarizationSharp;
using NemotronDiarizationSharp.Native;
using Xunit;
using Xunit.Abstractions;

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
            throw Xunit.Sdk.SkipException.ForSkip("Set NEMOTONDIARIZATIONSHARP_MODEL and NEMOTONDIARIZATIONSHARP_PCM_F32LE to run native CPU inference.");

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
            throw Xunit.Sdk.SkipException.ForSkip("Set NEMOTONDIARIZATIONSHARP_MODEL and NEMOTONDIARIZATIONSHARP_PCM_F32LE to run native CPU inference.");

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
            throw Xunit.Sdk.SkipException.ForSkip("The native integration tests require a Windows x64 process.");
    }
}
