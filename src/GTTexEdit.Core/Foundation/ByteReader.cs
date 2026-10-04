using System.Buffers.Binary;
using System.Text;

namespace GTTexEdit.Core;

/// <summary>
/// Positioned reader over a byte[] with switchable endianness. Replaces Syroot's BinaryStream for
/// every format this tool parses (all inputs are fully in memory).
/// </summary>
public sealed class ByteReader
{
    private readonly byte[] _data;

    public ByteReader(byte[] data, bool bigEndian = false, int position = 0)
    {
        _data = data;
        BigEndian = bigEndian;
        Position = position;
    }

    public bool BigEndian { get; set; }
    public int Position { get; set; }
    public int Length => _data.Length;
    public int Remaining => _data.Length - Position;
    public byte[] Buffer => _data;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || Position < 0 || Position + count > _data.Length)
            throw new EndOfStreamException($"Read of {count} byte(s) at 0x{Position:X} runs past the end of the data (0x{_data.Length:X}).");
        var span = _data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public byte ReadByte() => Take(1)[0];
    public sbyte ReadSByte() => (sbyte)Take(1)[0];

    public ushort ReadUInt16() => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short ReadInt16() => (short)ReadUInt16();
    public uint ReadUInt32() => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Take(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int ReadInt32() => (int)ReadUInt32();
    public ulong ReadUInt64() => BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(Take(8)) : BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public long ReadInt64() => (long)ReadUInt64();
    public float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());

    public byte[] ReadBytes(int count) => Take(count).ToArray();
    public ReadOnlySpan<byte> ReadSpan(int count) => Take(count);

    /// <summary>Reads <paramref name="count"/> raw bytes as ASCII (used for 4-byte magics).</summary>
    public string ReadAscii(int count) => Encoding.ASCII.GetString(Take(count));

    /// <summary>Reads a NUL-terminated string at the current position and advances past the terminator.</summary>
    public string ReadCString(Encoding? encoding = null)
    {
        int start = Position;
        int end = Array.IndexOf(_data, (byte)0, start);
        if (end < 0)
            end = _data.Length;
        Position = Math.Min(end + 1, _data.Length);
        return (encoding ?? Encoding.UTF8).GetString(_data, start, end - start);
    }

    /// <summary>Reads a NUL-terminated string at an absolute offset without moving <see cref="Position"/>.</summary>
    public string ReadCStringAt(int offset, Encoding? encoding = null)
    {
        int saved = Position;
        Position = offset;
        try { return ReadCString(encoding); }
        finally { Position = saved; }
    }

    public void Skip(int count) => Position += count;

    public void Align(int alignment)
    {
        int rem = Position % alignment;
        if (rem != 0)
            Position += alignment - rem;
    }
}
