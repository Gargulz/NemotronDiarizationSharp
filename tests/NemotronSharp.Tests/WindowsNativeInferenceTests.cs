using NemotronSharp;
using Xunit;

namespace NemotronSharp.Tests;

public sealed class WindowsNativeInferenceTests
{
    [Fact]
    public void CpuRuntimeRunsInferenceWhenModelAndPcmPathsAreConfigured()
    {
        if (!OperatingSystem.IsWindows() || Environment.Is64BitProcess is false)
            throw Xunit.Sdk.SkipException.ForSkip("The native smoke test requires a Windows x64 process.");

        string? modelPath = Environment.GetEnvironmentVariable("NEMOTONSHARP_MODEL");
        string? pcmPath = Environment.GetEnvironmentVariable("NEMOTONSHARP_PCM_F32LE");
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(pcmPath))
            throw Xunit.Sdk.SkipException.ForSkip("Set NEMOTONSHARP_MODEL and NEMOTONSHARP_PCM_F32LE to run native CPU inference.");

        byte[] bytes = File.ReadAllBytes(pcmPath);
        Assert.NotEmpty(bytes);
        Assert.Equal(0, bytes.Length % sizeof(float));
        float[] samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);

        using var model = DiarizationModel.Load(modelPath);
        Assert.StartsWith("nemo-speech-asr 0.2.0", DiarizationModel.NativeRuntimeVersion, StringComparison.Ordinal);
        Assert.True(model.SpeakerCount > 0);
        Assert.True(model.SecondsPerFrame > 0);

        using var stream = model.OpenStream();
        stream.Push(samples, sampleRate: 16000);
        stream.Finish();
        DiarizationSnapshot result = stream.GetSnapshot();

        Assert.True(result.IsFinal);
        Assert.True(result.FrameCount > 0);
        Assert.Equal(model.SpeakerCount, result.SpeakerCount);
        float probability = result.GetProbability(result.FirstProbabilityFrame, 0);
        Assert.InRange(probability, 0f, 1f);
    }
}
