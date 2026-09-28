using System.Buffers.Binary;
using System.Text;

namespace GTTexEdit.Core;

/// <summary>
/// Growable, seekable in-memory writer with switchable endianness. Seeking backwards and writing
/// patches bytes in place (it never truncates), so "reserve a table, fill it in later" works the way
/// the original file-based writers did.
/// </summary>
public sealed class ByteWriter
{
    private byte[] _buffer;
    private int _length;

    public ByteWriter(bool bigEndian = false, int capacity = 4096)
    {
        BigEndian = bigEndian;
        _buffer = new byte[Math.Max(capacity, 16)];
    }

    public bool BigEndian { get; set; }
    public int Position { get; set; }
    public int Length => _length;

    private Span<byte> Reserve(int count)
    {
        int end = checked(Position + count);
        if (end > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(end, _buffer.Length * 2));
        var span = _buffer.AsSpan(Position, count);
        Position = end;
        if (end > _length)
            _length = end;
        return span;
    }

    public void WriteByte(byte value) => Reserve(1)[0] = value;
    public void WriteSByte(sbyte value) => Reserve(1)[0] = (byte)value;

    public void WriteUInt16(ushort value)
    {
        if (BigEndian) BinaryPrimitives.WriteUInt16BigEndian(Reserve(2), value);
        else BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);
    }

    public void WriteInt16(short value) => WriteUInt16((ushort)value);

    public void WriteUInt32(uint value)
    {
        if (BigEndian) BinaryPrimitives.WriteUInt32BigEndian(Reserve(4), value);
        else BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);
    }

    public void WriteInt32(int value) => WriteUInt32((uint)value);

    public void WriteUInt64(ulong value)
    {
        if (BigEndian) BinaryPrimitives.WriteUInt64BigEndian(Reserve(8), value);
        else BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);
    }

    public void WriteInt64(long value) => WriteUInt64((ulong)value);
    public void WriteSingle(float value) => WriteInt32(BitConverter.SingleToInt32Bits(value));

    public void WriteBytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));

    /// <summary>Writes the raw ASCII bytes of <paramref name="text"/> with no terminator (magics).</summary>
    public void WriteAscii(string text) => Encoding.ASCII.GetBytes(text, Reserve(text.Length));

    /// <summary>Writes <paramref name="text"/> followed by a NUL terminator.</summary>
    public void WriteCString(string text, Encoding? encoding = null)
    {
        WriteBytes((encoding ?? Encoding.UTF8).GetBytes(text));
        WriteByte(0);
    }

    public void WriteFill(int count, byte value = 0)
    {
        if (count > 0)
            Reserve(count).Fill(value);
    }

    /// <summary>Pads with <paramref name="fill"/> until <see cref="Position"/> is a multiple of <paramref name="alignment"/>.</summary>
    public void Align(int alignment, byte fill = 0)
    {
        int rem = Position % alignment;
        if (rem != 0)
            WriteFill(alignment - rem, fill);
    }

    /// <summary>Moves to the end of everything written so far.</summary>
    public void SeekEnd() => Position = _length;

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);
}
