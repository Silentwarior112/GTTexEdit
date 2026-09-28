using System.Buffers.Binary;

namespace GTTexEdit.Core.Gs;

/// <summary>
/// Writes a texture set out from scratch: its registers, the GS memory it uploads, and the clut patch sets that
/// switch its palettes. This is what lets a texture change SIZE - a bigger image needs the set laid out again -
/// and it is also how a set is repacked once resizing has left dead blocks behind.
///
/// The layout is the one the originals use and is settled by measurement over 3,226 shipped sets: a 0x30 header,
/// the pglu textures at 0x30, the transfer table right behind them, each transfer's data at the next 0x10 boundary
/// in order, then the clut patch region, and the length field is the end rounded up to 0x10. The originals
/// sometimes leave more slack than that (82 sets pad the length field further, and the first transfer's data can
/// sit anywhere from the next 0x10 boundary to the next 0x100 one), so a set written here is not always the same
/// BYTES as the one it was read from - which is why what has to be proven of it is that every view still decodes
/// to the same picture, not that the file is identical.
/// </summary>
internal static class Tex1Builder
{
    /// <summary>Bytes in a GS block, and the blocks in one PSMCT32 page.</summary>
    private const int BlockBytes = 256, PageBlocks = 32;

    /// <summary>
    /// The transfers a set of this many blocks is uploaded by. Measured over the shipped sets: whole pages go up as
    /// ONE transfer of 64 by 32 per page, and what is left over is a binary decomposition - 16 blocks as 32x32, 8
    /// as 32x16, 4 as 16x16, 2 as 16x8 and 1 as 8x8 - every one of them PSMCT32 with a buffer width of 1, laid end
    /// to end from block 0. 363 of the 410 block counts in the sample are written exactly this way.
    /// </summary>
    public static List<(int Width, int Height)> Transfers(int blocks)
    {
        var transfers = new List<(int Width, int Height)>();
        if (blocks <= 0)
            return transfers;

        int pages = blocks / PageBlocks;
        if (pages > 0)
            transfers.Add((64, 32 * pages));

        foreach (var (size, width, height) in Tail)
        {
            if ((blocks % PageBlocks & size) != 0)
                transfers.Add((width, height));
        }
        return transfers;
    }

    /// <summary>What each power of two blocks looks like as a PSMCT32 transfer.</summary>
    private static readonly (int Blocks, int Width, int Height)[] Tail =
    [
        (16, 32, 32), (8, 32, 16), (4, 16, 16), (2, 16, 8), (1, 8, 8),
    ];

    /// <summary>
    /// A whole texture set. <paramref name="memory"/> is the GS memory it uploads, of which the first
    /// <paramref name="blocks"/> blocks are written out.
    /// </summary>
    public static byte[] Write(
        IReadOnlyList<PgluTexture> textures,
        GsMemory memory,
        int blocks,
        IReadOnlyList<byte[]> clutPatchSets,
        uint clutAnimation = 0)
    {
        ArgumentNullException.ThrowIfNull(textures);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(clutPatchSets);

        List<(int Width, int Height)> transfers = Transfers(blocks);
        int texturesOffset = Tex1Reader.HeaderSize;
        int transfersOffset = texturesOffset + textures.Count * PgluTexture.Size;
        int dataOffset = Align(transfersOffset + transfers.Count * Tex1Reader.TransferInfoSize, 0x10);

        // how long the whole thing will be, so it can be written in one pass
        int at = dataOffset;
        var places = new List<(int At, int Bp, int Width, int Height)>();
        int bp = 0;
        foreach (var (width, height) in transfers)
        {
            places.Add((at, bp, width, height));
            int size = width * height * 4;
            at = Align(at + size, 0x10);
            bp += size / BlockBytes;
        }

        int clutOffset = clutPatchSets.Count > 0 ? at : 0;
        if (clutPatchSets.Count > 0)
        {
            at += 4 + clutPatchSets.Count * 4;
            at += clutPatchSets.Sum(s => s.Length);
        }
        int length = Align(at, 0x10);
        var bytes = new byte[length];

        // the header
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Tex1Reader.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x0C), length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x12), (ushort)blocks);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x14), (ushort)textures.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x16), (ushort)transfers.Count);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x18), texturesOffset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x1C), transfersOffset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x20), clutOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x24), clutAnimation);

        for (int i = 0; i < textures.Count; i++)
            textures[i].Write(bytes.AsSpan(texturesOffset + i * PgluTexture.Size, PgluTexture.Size));

        // the transfer table, and the memory each one carries
        for (int i = 0; i < places.Count; i++)
        {
            var (dataAt, block, width, height) = places[i];
            int record = transfersOffset + i * Tex1Reader.TransferInfoSize;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record), dataAt);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(record + 4), (ushort)block);
            bytes[record + 6] = 1;                          // buffer width
            bytes[record + 7] = (byte)GsPsm.PSMCT32;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(record + 8), (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(record + 10), (ushort)height);

            Span<uint> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                bytes.AsSpan(dataAt, width * height * 4));
            memory.ReadCT32(block, 1, width, height, words);
        }

        // the clut patch sets: a count, one offset each, then the bodies
        if (clutPatchSets.Count > 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(clutOffset), clutPatchSets.Count);
            int body = clutOffset + 4 + clutPatchSets.Count * 4;
            for (int i = 0; i < clutPatchSets.Count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(clutOffset + 4 + i * 4), body);
                clutPatchSets[i].CopyTo(bytes.AsSpan(body));
                body += clutPatchSets[i].Length;
            }
        }
        return bytes;
    }

    private static int Align(int value, int to) => (value + to - 1) & ~(to - 1);
}
