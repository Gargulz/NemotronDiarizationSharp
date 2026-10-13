using System.Buffers.Binary;

namespace NemotronDiarizationSharp.Examples;

internal sealed class WavePcmReader : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly ushort _format;
    private readonly int _bits;
    private readonly byte[] _frame;
    private long _remainingFrames;
    private bool _disposed;
    private bool _failed;

    public WavePcmReader(string path) : this(File.OpenRead(path), leaveOpen: false) { }

    public WavePcmReader(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _leaveOpen = leaveOpen;
        try
        {
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("WAVE input must be a readable, seekable stream.", nameof(stream));
            stream.Position = 0;
            Span<byte> header = stackalloc byte[12];
            ReadExactly(header);
            if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
                throw new InvalidDataException("Only little-endian RIFF/WAVE is supported; RIFX and RF64 are unsupported.");
            long riffEnd = checked(8L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]));
            if (riffEnd < 12 || riffEnd > stream.Length)
                throw new InvalidDataException("The RIFF container length is invalid or truncated.");

            bool hasFormat = false;
            long dataOffset = -1;
            long dataLength = 0;
            ushort blockAlign = 0;
            Span<byte> chunkHeader = stackalloc byte[8];
            Span<byte> format = stackalloc byte[16];
            while (stream.Position < riffEnd)
            {
                if (riffEnd - stream.Position < 8)
                    throw new InvalidDataException("Incomplete WAVE chunk header inside the RIFF container.");
                ReadExactly(chunkHeader);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
                long chunkEnd = checked(stream.Position + size);
                long paddedEnd = checked(chunkEnd + (size & 1));
                if (paddedEnd > riffEnd)
                    throw new InvalidDataException("WAVE chunk or padding extends beyond the RIFF container.");
                if (chunkHeader[..4].SequenceEqual("fmt "u8))
                {
                    if (hasFormat) throw new InvalidDataException("Duplicate WAVE format chunk.");
                    if (size < 16) throw new InvalidDataException("WAVE format chunk is too short.");
                    ReadExactly(format);
                    _format = BinaryPrimitives.ReadUInt16LittleEndian(format);
                    Channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
                    uint rate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
                    uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
                    _bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
                    if (_format == 0xfffe)
                        throw new InvalidDataException("WAVE_FORMAT_EXTENSIBLE is unsupported; convert to PCM or IEEE float32 WAVE.");
                    if (_format is not 1 and not 3 || (_format == 1 && _bits is not 8 and not 16 and not 24 and not 32)
                        || (_format == 3 && _bits != 32))
                        throw new InvalidDataException("Supported WAVE formats are PCM 8/16/24/32-bit and IEEE float32.");
                    if (Channels == 0 || rate < 8000 || rate > 96000)
                        throw new InvalidDataException("WAVE requires at least one channel and a sample rate of 8000-96000 Hz.");
                    int expectedAlign = checked(Channels * (_bits / 8));
                    long expectedByteRate = checked((long)rate * expectedAlign);
                    if (blockAlign != expectedAlign || byteRate != expectedByteRate)
                        throw new InvalidDataException("WAVE block alignment or byte rate is invalid.");
                    SampleRate = checked((int)rate);
                    hasFormat = true;
                }
                else if (chunkHeader[..4].SequenceEqual("data"u8))
                {
                    if (dataOffset >= 0) throw new InvalidDataException("Duplicate WAVE data chunk.");
                    dataOffset = stream.Position;
                    dataLength = size;
                }
                stream.Position = paddedEnd;
            }
            if (!hasFormat || dataOffset < 0)
                throw new InvalidDataException("WAVE format or data chunk is missing.");
            if (dataLength == 0 || dataLength % blockAlign != 0)
                throw new InvalidDataException("WAVE data must contain complete, nonempty audio frames.");
            TotalFrames = dataLength / blockAlign;
            _remainingFrames = TotalFrames;
            // Scratch storage holds one interleaved frame, independent of recording duration.
            _frame = new byte[blockAlign];
            stream.Position = dataOffset;
        }
        catch
        {
            if (!leaveOpen) stream.Dispose();
            throw;
        }
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public long TotalFrames { get; }

    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed) throw new InvalidOperationException("WAVE reader cannot continue after a decoding failure.");
        int frames = (int)Math.Min(destination.Length, _remainingFrames);
        try
        {
            for (int index = 0; index < frames; index++)
            {
                ReadExactly(_frame);
                double sum = 0;
                int bytesPerSample = _bits / 8;
                for (int channel = 0; channel < Channels; channel++)
                    sum += Decode(_frame.AsSpan(channel * bytesPerSample, bytesPerSample));
                destination[index] = (float)(sum / Channels);
                _remainingFrames--;
            }
            return frames;
        }
        catch
        {
            _failed = true;
            throw;
        }
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
            8 => (sample[0] - 128) / 128d,
            16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768d,
            24 => Decode24(sample) / 8388608d,
            32 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648d,
            _ => throw new InvalidDataException("Unsupported PCM sample width.")
        };
    }

    private static int Decode24(ReadOnlySpan<byte> sample)
    {
        int value = sample[0] | sample[1] << 8 | sample[2] << 16;
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xff000000);
    }

    private void ReadExactly(Span<byte> buffer)
    {
        try { _stream.ReadExactly(buffer); }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("WAVE input is truncated.", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_leaveOpen) _stream.Dispose();
    }
}
