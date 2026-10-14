using System.Runtime.CompilerServices;

namespace NemotronDiarizationSharp.Examples;

/// <summary>Runnable building blocks for the Nemotron 3 reference workflows.</summary>
public static class Nemotron3ReferenceExamples
{
    public static DiarizationSnapshot RunCompletedWave(string modelPath, string wavePath)
    {
        using var model = DiarizationModel.LoadForRecording(modelPath);
        return model.ProcessWaveFile(wavePath);
    }

    public static DiarizationSnapshot RunDecodedPcm(float[] monoPcm, int sampleRate, string modelPath)
    {
        ArgumentNullException.ThrowIfNull(monoPcm);
        using var model = DiarizationModel.LoadForRecording(modelPath);
        return model.ProcessOfflineWithResults(monoPcm, sampleRate,
            new DiarizationResultOptions
            {
                SegmentationMode = DiarizationSegmentationMode.NvidiaNeMoReference
            });
    }

    public static async Task<DiarizationResultUpdate> RunLiveWaveAsync(string modelPath, string wavePath,
        Action<DiarizationResultUpdate>? onUpdate = null, CancellationToken cancellationToken = default)
    {
        using var model = DiarizationModel.LoadForStreaming(modelPath);
        using var stream = model.OpenStream();
        using var reader = new DiarizationWaveReader(wavePath);
        DiarizationResultUpdate? final = null;
        await foreach (DiarizationResultUpdate update in stream.ProcessStreamAsync(
            ReadWaveChunksAsync(reader, 250, cancellationToken), reader.SampleRate, cancellationToken)
            .ConfigureAwait(false))
        {
            onUpdate?.Invoke(update);
            final = update;
        }
        return final ?? throw new InvalidOperationException("The WAV source produced no final update.");
    }

    public static DiarizationSnapshot RunCustomizedWave(string modelPath, string wavePath)
    {
        using var model = DiarizationModel.LoadForRecording(modelPath,
            new DiarizationModelOptions { ChunkFrames = 340, RightContextFrames = 40 });
        return model.ProcessWaveFile(wavePath, new DiarizationResultOptions
        {
            SegmentationMode = DiarizationSegmentationMode.NvidiaNeMoReference,
            NeMoReferenceOptions = new DiarizationPostprocessingOptions
            {
                Onset = 0.55f,
                Offset = 0.45f,
                PadOnsetSeconds = 0.02,
                PadOffsetSeconds = 0.02,
                MinGapSeconds = 0.10,
                MinDurationSeconds = 0.08
            }
        });
    }

    public static DiarizationSnapshot RunNativeWave(string modelPath, string wavePath)
    {
        using var model = DiarizationModel.LoadForRecording(modelPath);
        return model.ProcessWaveFile(wavePath, new DiarizationResultOptions
        {
            SegmentationMode = DiarizationSegmentationMode.Native
        });
    }

    public static IReadOnlyList<DiarizationSegment> RunApplicationPostprocessor(DiarizationFrameBatch probabilities,
        DiarizationPostprocessingOptions options)
        => DiarizationPostprocessor.Segment(probabilities, options);

    public static void ExportRttm(TextWriter writer, string recordingId, DiarizationSnapshot result)
        => DiarizationPostprocessor.WriteRttm(writer, result.Segments, recordingId);

    private static async IAsyncEnumerable<ReadOnlyMemory<float>> ReadWaveChunksAsync(DiarizationWaveReader reader,
        int chunkMilliseconds, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new float[Math.Max(1, checked(reader.SampleRate * chunkMilliseconds / 1000))];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = reader.Read(buffer);
            if (count == 0)
                yield break;
            var owned = new float[count];
            Array.Copy(buffer, owned, count);
            yield return owned;
            await Task.Yield();
        }
    }
}
