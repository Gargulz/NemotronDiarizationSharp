using System.Buffers.Binary;

namespace NemotronDiarizationSharp.NativeTestHost;

internal sealed class PcmInputReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly AudioInputDescription _description;
    private readonly string _format;

    private PcmInputReader(NativeTestRequest request, AudioInputDescription description)
    {
        _description = description;
        _format = request.InputFormat;
        long startFrame = checked((long)Math.Round(request.AudioStartSeconds * request.SampleRate,
            MidpointRounding.AwayFromZero));
        _stream = File.OpenRead(request.AudioPath);
        _stream.Position = checked(description.DataOffset + startFrame * description.BytesPerSample);
    }

    public static AudioInputDescription Inspect(NativeTestRequest request)
    {
        if (string.Equals(request.InputFormat, "pcmF32Le", StringComparison.Ordinal))
        {
            if (request.AudioStartSeconds != 0)
                throw new InvalidDataException("Raw pcmF32Le input supports only AudioStartSeconds=0.");
            long length = new FileInfo(request.AudioPath).Length;
            if (length == 0 || length % sizeof(float) != 0)
                throw new InvalidDataException("Raw float32 PCM file length must be a positive multiple of four bytes.");
            return new AudioInputDescription(0, length / sizeof(float), request.SampleRate, sizeof(float));
        }

        if (!string.Equals(request.InputFormat, "wavPcm16Le", StringComparison.Ordinal))
            throw new InvalidDataException("InputFormat must be 'pcmF32Le' or 'wavPcm16Le'.");

        using var stream = File.OpenRead(request.AudioPath);
        using var reader = new BinaryReader(stream);
        if (ReadTag(reader) != "RIFF")
            throw new InvalidDataException("WAV input must use a RIFF container.");
        _ = reader.ReadUInt32();
        if (ReadTag(reader) != "WAVE")
            throw new InvalidDataException("RIFF input is not WAVE audio.");

        ushort? format = null;
        ushort? channels = null;
        int? sampleRate = null;
        ushort? blockAlign = null;
        ushort? bits = null;
        long dataOffset = -1;
        long dataLength = -1;
        while (stream.Position + 8 <= stream.Length)
        {
            string tag = ReadTag(reader);
            uint chunkSize = reader.ReadUInt32();
            long chunkStart = stream.Position;
            long chunkEnd = checked(chunkStart + chunkSize);
            if (chunkEnd > stream.Length)
                throw new InvalidDataException($"WAV {tag} chunk extends beyond the file.");
            if (tag == "fmt ")
            {
                if (chunkSize < 16)
                    throw new InvalidDataException("WAV format chunk is shorter than the PCM header.");
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = checked((int)reader.ReadUInt32());
                _ = reader.ReadUInt32();
                blockAlign = reader.ReadUInt16();
                bits = reader.ReadUInt16();
            }
            else if (tag == "data" && dataOffset < 0)
            {
                dataOffset = chunkStart;
                dataLength = chunkSize;
            }
            stream.Position = chunkEnd + (chunkSize & 1);
        }

        if (format != 1 || channels != 1 || bits != 16 || blockAlign != 2 || sampleRate is null || dataOffset < 0)
            throw new InvalidDataException("WAV input must contain mono PCM16LE audio and a data chunk.");
        if (sampleRate != request.SampleRate || dataLength % blockAlign.Value != 0)
            throw new InvalidDataException("WAV sample rate or data block alignment does not match the request.");
        return new AudioInputDescription(dataOffset, dataLength / blockAlign.Value, sampleRate.Value, blockAlign.Value);
    }

    public static PcmInputReader Open(NativeTestRequest request, AudioInputDescription description) => new(request, description);

    public int ReadSamples(Span<float> destination, int requestedSamples)
    {
        if (requestedSamples < 0 || requestedSamples > destination.Length)
            throw new ArgumentOutOfRangeException(nameof(requestedSamples));
        if (_format == "pcmF32Le")
        {
            byte[] bytes = new byte[checked(requestedSamples * sizeof(float))];
            int byteCount = _stream.Read(bytes, 0, bytes.Length);
            if (byteCount % sizeof(float) != 0)
                throw new EndOfStreamException("Raw float32 PCM ended on a partial sample.");
            int count = byteCount / sizeof(float);
            for (int index = 0; index < count; index++)
            {
                float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                    bytes.AsSpan(index * sizeof(float), sizeof(float))));
                ValidateSample(value);
                destination[index] = value;
            }
            return count;
        }

        byte[] wavBytes = new byte[checked(requestedSamples * sizeof(short))];
        int readBytes = _stream.Read(wavBytes, 0, wavBytes.Length);
        if (readBytes % sizeof(short) != 0)
            throw new EndOfStreamException("PCM16 WAV ended on a partial sample.");
        int sampleCount = readBytes / sizeof(short);
        for (int index = 0; index < sampleCount; index++)
            destination[index] = BinaryPrimitives.ReadInt16LittleEndian(wavBytes.AsSpan(index * sizeof(short), sizeof(short))) / 32768f;
        return sampleCount;
    }

    public void Dispose() => _stream.Dispose();

    private static string ReadTag(BinaryReader reader) => System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));

    private static void ValidateSample(float value)
    {
        if (!float.IsFinite(value) || value is < -1 or > 1)
            throw new InvalidDataException("Raw float32 PCM samples must be finite and in [-1, 1].");
    }
}
