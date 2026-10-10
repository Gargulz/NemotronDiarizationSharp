using NemotronDiarizationSharp;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: NemotronDiarizationSharp.Console <model.gguf> <mono-pcm-f32le.raw> <sample-rate>");
    return 2;
}

if (!int.TryParse(args[2], out int sampleRate) || sampleRate is < 8000 or > 96000)
{
    Console.Error.WriteLine("Sample rate must be between 8000 and 96000 Hz.");
    return 2;
}

byte[] bytes = File.ReadAllBytes(args[1]);
if (bytes.Length % sizeof(float) != 0)
{
    Console.Error.WriteLine("The PCM file length must be divisible by four bytes.");
    return 2;
}

if (!BitConverter.IsLittleEndian)
{
    Console.Error.WriteLine("This example expects little-endian float32 PCM.");
    return 2;
}

float[] samples = new float[bytes.Length / sizeof(float)];
Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);

using var model = DiarizationModel.Load(args[0]);
using var stream = model.OpenStream();
stream.Push(samples, sampleRate);
stream.Finish();

DiarizationSnapshot result = stream.GetSnapshot();
Console.WriteLine($"NeMo-Speech.cpp: {DiarizationModel.NativeRuntimeVersion}");
Console.WriteLine($"Speakers: {result.SpeakerCount}; frames: {result.FrameCount}; frame step: {result.SecondsPerFrame:F3}s; final: {result.IsFinal}");
long displayedFrames = 0;
for (long frame = result.FirstProbabilityFrame; frame < result.FrameCount && displayedFrames < 5; frame++, displayedFrames++)
{
    string probabilities = string.Join(", ", Enumerable.Range(0, result.SpeakerCount)
        .Select(speaker => $"spk{speaker + 1}={result.GetProbability(frame, speaker):F2}"));
    Console.WriteLine($"{result.GetFrameTimestamp(frame):F2}s: {probabilities}");
}
foreach (DiarizationSegment segment in result.Segments)
    Console.WriteLine($"speaker {segment.Speaker}: {segment.StartTime:F2}s - {segment.EndTime:F2}s");

return 0;
