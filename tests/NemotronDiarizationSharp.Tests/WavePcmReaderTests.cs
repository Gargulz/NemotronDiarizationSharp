using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class DiarizationWaveReaderTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void DecodesPcmExtremaZeroAndSignedValues(int bits)
    {
        long minimum = bits == 8 ? 0 : -(1L << (bits - 1));
        long maximum = bits == 8 ? 255 : (1L << (bits - 1)) - 1;
        long zero = bits == 8 ? 128 : 0;
        long half = bits == 8 ? 64 : -(1L << (bits - 2));
        using var stream = new MemoryStream(Wave(Format(1, bits), Pcm(bits, minimum, zero, maximum, half)));
        using var reader = new DiarizationWaveReader(stream);
        float[] samples = new float[4];
        Assert.Equal(4, reader.Read(samples));
        Assert.Equal(-1f, samples[0]);
        Assert.Equal(0f, samples[1]);
        Assert.Equal((float)(1 - 1d / (1L << (bits - 1))), samples[2]);
        Assert.Equal(-0.5f, samples[3]);
        Assert.Equal(16000, reader.SampleRate);
        Assert.Equal(1, reader.Channels);
        Assert.Equal(4, reader.TotalFrames);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void DownmixesByArithmeticMean(int channels)
    {
        long[] values = Enumerable.Range(0, channels).Select(index => index == 0 ? 16384L : -8192L).ToArray();
        using var reader = new DiarizationWaveReader(new MemoryStream(Wave(Format(1, 16, channels), Pcm(16, values))));
        float[] destination = new float[1];
        Assert.Equal(1, reader.Read(destination));
        Assert.Equal((float)((0.5 - (channels - 1) * 0.25) / channels), destination[0]);
    }

    [Fact]
    public void FloatClipsEachChannelBeforeAveraging()
    {
        using var reader = new DiarizationWaveReader(new MemoryStream(Wave(Format(3, 32, 2), Floats(100, -0.5f, -100, 0.5f, 0.25f, 0.75f))));
        float[] destination = new float[3];
        Assert.Equal(3, reader.Read(destination));
        Assert.Equal(new[] { 0.25f, -0.25f, 0.5f }, destination);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void RejectsNonfiniteFloatAndCannotContinue(float value)
    {
        using var reader = new DiarizationWaveReader(new MemoryStream(Wave(Format(3, 32), Floats(value, 0))));
        Assert.Throws<InvalidDataException>(() => reader.Read(new float[1]));
        Assert.Throws<InvalidOperationException>(() => reader.Read(new float[1]));
    }

    [Fact]
    public void SequentialReadsRespectDestinationAndDataBoundaries()
    {
        byte[] bytes = Wave(Format(1, 16), Pcm(16, 0, 8192, 16384), ("JUNK", new byte[5]));
        using var reader = new DiarizationWaveReader(new ShortReadStream(bytes));
        Assert.Equal(0, reader.Read(Span<float>.Empty));
        float[] destination = new float[2];
        Assert.Equal(2, reader.Read(destination));
        Assert.Equal(new[] { 0f, 0.25f }, destination);
        destination[1] = 123;
        Assert.Equal(1, reader.Read(destination));
        Assert.Equal(0.5f, destination[0]);
        Assert.Equal(123, destination[1]);
        Assert.Equal(0, reader.Read(destination));
    }

    [Fact]
    public void HandlesOddUnknownChunksAndDataBeforeFormat()
    {
        byte[] bytes = Riff(("JUNK", new byte[] { 1, 2, 3 }), ("data", Pcm(8, 128)), ("fmt ", Format(1, 8)), ("LIST", new byte[1]));
        using var reader = new DiarizationWaveReader(new MemoryStream(bytes));
        float[] destination = new float[1];
        Assert.Equal(1, reader.Read(destination));
        Assert.Equal(0f, destination[0]);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(96000)]
    public void AcceptsSampleRateBoundaries(int rate)
    {
        using var reader = new DiarizationWaveReader(new MemoryStream(Wave(Format(1, 16, rate: rate), Pcm(16, 0))));
        Assert.Equal(rate, reader.SampleRate);
    }

    [Theory]
    [InlineData("compressed")]
    [InlineData("extensible")]
    [InlineData("pcm-width")]
    [InlineData("float-width")]
    [InlineData("channels")]
    [InlineData("low-rate")]
    [InlineData("high-rate")]
    [InlineData("uint-rate")]
    [InlineData("align")]
    [InlineData("byte-rate")]
    [InlineData("align-overflow")]
    [InlineData("byte-rate-overflow")]
    public void RejectsInvalidOrUnsupportedFormat(string defect)
    {
        byte[] format = Format(1, 16);
        switch (defect)
        {
            case "compressed": Set16(format, 0, 6); break;
            case "extensible": Set16(format, 0, 0xfffe); break;
            case "pcm-width": Set16(format, 14, 12); break;
            case "float-width": Set16(format, 0, 3); Set16(format, 14, 16); break;
            case "channels": Set16(format, 2, 0); break;
            case "low-rate": Set32(format, 4, 7999); break;
            case "high-rate": Set32(format, 4, 96001); break;
            case "uint-rate": Set32(format, 4, uint.MaxValue); break;
            case "align": Set16(format, 12, 1); break;
            case "byte-rate": Set32(format, 8, 1); break;
            case "align-overflow": Set16(format, 2, ushort.MaxValue); Set16(format, 14, 32); break;
            case "byte-rate-overflow": Set16(format, 2, 60000); Set16(format, 14, 8); Set16(format, 12, 60000); Set32(format, 4, 96000); break;
        }
        Assert.Throws<InvalidDataException>(() => new DiarizationWaveReader(new MemoryStream(Wave(format, Pcm(16, 0)))));
    }

    [Theory]
    [InlineData("missing-format")]
    [InlineData("missing-data")]
    [InlineData("duplicate-format")]
    [InlineData("duplicate-data")]
    [InlineData("short-format")]
    [InlineData("partial-frame")]
    [InlineData("empty-data")]
    [InlineData("riff-short")]
    [InlineData("riff-long")]
    [InlineData("chunk-long")]
    [InlineData("missing-pad")]
    [InlineData("partial-chunk-header")]
    [InlineData("wrong-wave")]
    [InlineData("rifx")]
    [InlineData("rf64")]
    public void RejectsMalformedContainer(string defect)
    {
        byte[] format = Format(1, 16);
        byte[] data = Pcm(16, 0);
        byte[] bytes = defect switch
        {
            "missing-format" => Riff(("data", data)),
            "missing-data" => Riff(("fmt ", format)),
            "duplicate-format" => Riff(("fmt ", format), ("fmt ", format), ("data", data)),
            "duplicate-data" => Wave(format, data, ("data", data)),
            "short-format" => Wave(new byte[15], data),
            "partial-frame" => Wave(format, new byte[3]),
            "empty-data" => Wave(format, []),
            "missing-pad" => Wave(Format(1, 8), new byte[1])[..^1],
            _ => Wave(format, data)
        };
        switch (defect)
        {
            case "riff-short": Set32(bytes, 4, 3); break;
            case "riff-long": Set32(bytes, 4, uint.MaxValue); break;
            case "chunk-long": Set32(bytes, 16, uint.MaxValue); break;
            case "missing-pad": Set32(bytes, 4, (uint)bytes.Length - 8); break;
            case "partial-chunk-header": Array.Resize(ref bytes, bytes.Length + 1); Set32(bytes, 4, (uint)bytes.Length - 8); break;
            case "wrong-wave": bytes[8] = (byte)'X'; break;
            case "rifx": bytes[3] = (byte)'X'; break;
            case "rf64": "RF64"u8.CopyTo(bytes); break;
        }
        Assert.Throws<InvalidDataException>(() => new DiarizationWaveReader(new MemoryStream(bytes)));
    }

    [Fact]
    public void RejectsEveryTruncatedPrefix()
    {
        byte[] bytes = Wave(Format(1, 16), Pcm(16, 0, 1));
        for (int length = 0; length < bytes.Length; length++)
            Assert.Throws<InvalidDataException>(() => new DiarizationWaveReader(new MemoryStream(bytes[..length])));
    }

    [Fact]
    public void DoesNotTreatBytesOutsideRiffAsChunks()
    {
        byte[] bytes = Wave(Format(1, 16), Pcm(16, 0));
        Array.Resize(ref bytes, bytes.Length + 20);
        using var reader = new DiarizationWaveReader(new MemoryStream(bytes));
        Assert.Equal(1, reader.TotalFrames);
        // Moving the RIFF boundary before the data chunk cannot be rescued by trailing bytes.
        Set32(bytes, 4, 28);
        Assert.Throws<InvalidDataException>(() => new DiarizationWaveReader(new MemoryStream(bytes)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposesAccordingToOwnership(bool leaveOpen)
    {
        var stream = new MemoryStream(Wave(Format(1, 16), Pcm(16, 0)));
        var reader = new DiarizationWaveReader(stream, leaveOpen);
        reader.Dispose();
        reader.Dispose();
        Assert.Equal(leaveOpen, stream.CanRead);
        Assert.Throws<ObjectDisposedException>(() => reader.Read(new float[1]));
        stream.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructorFailureRespectsOwnership(bool leaveOpen)
    {
        var stream = new MemoryStream(new byte[1]);
        Assert.Throws<InvalidDataException>(() => new DiarizationWaveReader(stream, leaveOpen));
        Assert.Equal(leaveOpen, stream.CanRead);
        stream.Dispose();
    }

    [Fact]
    public void RejectsUnreadableOrUnseekableStreams()
    {
        using var disposed = new MemoryStream();
        disposed.Dispose();
        Assert.Throws<ArgumentException>(() => new DiarizationWaveReader(disposed, true));
        using var unseekable = new UnseekableStream();
        Assert.Throws<ArgumentException>(() => new DiarizationWaveReader(unseekable, true));
        Assert.Throws<ArgumentNullException>(() => new DiarizationWaveReader((Stream)null!));
    }

    [Fact]
    public void DetectsDataTruncatedAfterConstruction()
    {
        using var stream = new MemoryStream(Wave(Format(1, 16), Pcm(16, 0, 1)));
        using var reader = new DiarizationWaveReader(stream, true);
        stream.SetLength(stream.Length - 1);
        Assert.Throws<InvalidDataException>(() => reader.Read(new float[2]));
        Assert.Throws<InvalidOperationException>(() => reader.Read(new float[1]));
    }

    [Fact]
    public void PathConstructorOwnsAndReleasesFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nds-wave-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(path, Wave(Format(1, 16), Pcm(16, 0)));
            using (var reader = new DiarizationWaveReader(path)) Assert.Equal(1, reader.Read(new float[1]));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    private static byte[] Format(int tag, int bits, int channels = 1, int rate = 16000)
    {
        byte[] format = new byte[16];
        Set16(format, 0, (ushort)tag);
        Set16(format, 2, (ushort)channels);
        Set32(format, 4, (uint)rate);
        Set32(format, 8, (uint)(rate * channels * (bits / 8)));
        Set16(format, 12, (ushort)(channels * (bits / 8)));
        Set16(format, 14, (ushort)bits);
        return format;
    }

    private static byte[] Wave(byte[] format, byte[] data, params (string Tag, byte[] Data)[] extra)
        => Riff(new[] { ("fmt ", format), ("data", data) }.Concat(extra).ToArray());

    private static byte[] Riff(params (string Tag, byte[] Data)[] chunks)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write("RIFF"u8);
        writer.Write(0u);
        writer.Write("WAVE"u8);
        foreach (var chunk in chunks)
        {
            writer.Write(Encoding.ASCII.GetBytes(chunk.Tag));
            writer.Write((uint)chunk.Data.Length);
            writer.Write(chunk.Data);
            if ((chunk.Data.Length & 1) != 0) writer.Write((byte)0);
        }
        byte[] bytes = stream.ToArray();
        Set32(bytes, 4, (uint)bytes.Length - 8);
        return bytes;
    }

    private static byte[] Pcm(int bits, params long[] values)
    {
        byte[] bytes = new byte[values.Length * (bits / 8)];
        for (int index = 0; index < values.Length; index++)
            for (int octet = 0; octet < bits / 8; octet++)
                bytes[index * (bits / 8) + octet] = (byte)(values[index] >> (8 * octet));
        return bytes;
    }

    private static byte[] Floats(params float[] values)
        => Pcm(32, values.Select(value => (long)BitConverter.SingleToInt32Bits(value)).ToArray());

    private static void Set16(byte[] bytes, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);

    private static void Set32(byte[] bytes, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(1, buffer.Length)]);
    }

    private sealed class UnseekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
