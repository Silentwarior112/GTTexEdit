using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GTTexEdit.Core.Gs;

/// <summary>
/// Emulated GS local memory: transfers are swizzled into it and textures / CLUTs are read back out, each in
/// its own storage mode. Needed because a texture's blocks do not have to come from one transfer of the same
/// format (multi-texture sets upload everything as PSMCT32 blobs, CLUTs sit in blocks the image leaves free).
/// Only as much memory as the set can reach is allocated, capped at the real 4 MB.
/// </summary>
internal sealed class GsMemory
{
    private const long MaxWords = (long)GsFormat.MaxBlocks * GsFormat.BlockWords;

    private readonly byte[] _memory; // little-endian words, exactly as the GS (and the transfer data) has them

    public GsMemory(long words) => _memory = new byte[Math.Clamp(words, 0, MaxWords) * 4];

    /// <summary>The memory itself: little-endian words in block order (block n starts at byte n * 256).</summary>
    public byte[] Raw => _memory;

    /// <summary>One past the last word a <paramref name="width"/> x <paramref name="height"/> rectangle at block <paramref name="bp"/> can touch.</summary>
    public static long WordsReached(GsFormat format, int bp, int pagesPerRow, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return 0;
        long lastPage = (width - 1) / format.PageWidth + (long)((height - 1) / format.PageHeight) * pagesPerRow;
        return (long)bp * GsFormat.BlockWords + (lastPage + 1) * GsFormat.PageWords;
    }

    /// <summary>
    /// Pages per buffer row for a buffer width given in 64-pixel units (TBW / a transfer's BW). PSMCT32/16 pages
    /// are 64 pixels wide; the indexed formats' pages are 128 wide, hence half as many (a BW of 1 gives 0 there:
    /// every page row then aliases the first one - kept, that is where the old tools put those pixels).
    /// </summary>
    public static int PagesPerRow(GsFormat format, int bufferWidth) => format.PageWidth == 128 ? bufferWidth >> 1 : bufferWidth;

    // Keeps Address() inside int range for any header values; real sets stay far below (4 MB is 2^20 words).
    private static int CheckedPagesPerRow(GsFormat format, int bp, int bufferWidth, int width, int height)
    {
        int pagesPerRow = PagesPerRow(format, bufferWidth);
        if (WordsReached(format, bp, pagesPerRow, width, height) > int.MaxValue / 8)
            throw new InvalidDataException($"A {width}x{height} {format.Psm} area at block {bp} lies outside GS memory.");
        return pagesPerRow;
    }

    // Address of pixel (x, y) in format units (words / halves / bytes / nibbles).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Address(GsFormat f, int bp, int pagesPerRow, int x, int y)
    {
        int page = (x >> f.PageShiftX) + (y >> f.PageShiftY) * pagesPerRow;
        int inPage = ((y & (f.PageHeight - 1)) << f.PageShiftX) | (x & (f.PageWidth - 1));
        return (bp * GsFormat.BlockWords + page * GsFormat.PageWords) * f.UnitsPerWord + f.PageTable[inPage];
    }

    /// <summary>Swizzles row-major transfer data of <paramref name="format"/> into memory (a GS host-to-local IMAGE transfer).</summary>
    public void Write(GsFormat format, int bp, int bufferWidth, int width, int height, ReadOnlySpan<byte> data)
    {
        int ppr = CheckedPagesPerRow(format, bp, bufferWidth, width, height);
        int i = 0;
        switch (format.UnitsPerWord)
        {
            case 1:
                {
                    Span<uint> words = MemoryMarshal.Cast<byte, uint>(_memory.AsSpan());
                    ReadOnlySpan<uint> src = MemoryMarshal.Cast<byte, uint>(data);
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                            words[Address(format, bp, ppr, x, y)] = src[i++];
                    break;
                }
            case 2:
                {
                    Span<ushort> halves = MemoryMarshal.Cast<byte, ushort>(_memory.AsSpan());
                    ReadOnlySpan<ushort> src = MemoryMarshal.Cast<byte, ushort>(data);
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                            halves[Address(format, bp, ppr, x, y)] = src[i++];
                    break;
                }
            case 4:
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        _memory[Address(format, bp, ppr, x, y)] = data[i++];
                break;

            default:
                // PSMT4 data is one continuous nibble stream, low nibble first; rows are NOT byte aligned.
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++, i++)
                    {
                        int nibble = (data[i >> 1] >> ((i & 1) * 4)) & 0xF;
                        int address = Address(format, bp, ppr, x, y);
                        int shift = (address & 1) * 4;
                        ref byte cell = ref _memory[address >> 1];
                        cell = (byte)((cell & ~(0xF << shift)) | (nibble << shift));
                    }
                }
                break;
        }
    }

    /// <summary>Reads a PSMCT32 rectangle. <paramref name="wordOffset"/> shifts every address (a CLUT's CSA: 8 words per step).</summary>
    public void ReadCT32(int bp, int bufferWidth, int width, int height, Span<uint> destination, int wordOffset = 0)
    {
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(_memory);
        int ppr = CheckedPagesPerRow(GsFormat.CT32, bp, bufferWidth, width, height);
        int i = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                destination[i++] = words[Address(GsFormat.CT32, bp, ppr, x, y) + wordOffset];
    }

    /// <summary>Reads a PSMCT16 rectangle. <paramref name="wordOffset"/> as in <see cref="ReadCT32"/>.</summary>
    public void ReadCT16(int bp, int bufferWidth, int width, int height, Span<ushort> destination, int wordOffset = 0)
    {
        ReadOnlySpan<ushort> halves = MemoryMarshal.Cast<byte, ushort>(_memory);
        int ppr = CheckedPagesPerRow(GsFormat.CT16, bp, bufferWidth, width, height);
        int i = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                destination[i++] = halves[Address(GsFormat.CT16, bp, ppr, x, y) + wordOffset * 2];
    }

    /// <summary>Reads a PSMT8 or PSMT4 rectangle as one palette index per destination byte.</summary>
    /// <summary>Puts index values into memory the way <see cref="ReadIndices"/> takes them out.</summary>
    public void WriteIndices(GsFormat format, int bp, int bufferWidth, int width, int height, ReadOnlySpan<byte> source)
    {
        int ppr = CheckedPagesPerRow(format, bp, bufferWidth, width, height);
        int i = 0;
        if (format.UnitsPerWord == 4)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    _memory[Address(format, bp, ppr, x, y)] = source[i++];
            return;
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int address = Address(format, bp, ppr, x, y);
                int shift = (address & 1) * 4;
                _memory[address >> 1] = (byte)((_memory[address >> 1] & ~(0xF << shift)) | ((source[i++] & 0xF) << shift));
            }
        }
    }

    /// <summary>
    /// Puts a palette where the CLUT at this block and slot is read from. CSA counts 32-byte slots, and the
    /// entries sit in the 8x2 (or 16x16) arrangement the GS reads them in - the same places the reader looks.
    /// </summary>
    public void WriteClut(int cbp, int csa, ReadOnlySpan<uint> colours)
    {
        Span<uint> words = MemoryMarshal.Cast<byte, uint>(_memory.AsSpan());
        int width = colours.Length == 256 ? 16 : 8;
        for (int i = 0; i < colours.Length; i++)
        {
            int stored = colours.Length == 256 ? Tex1Reader.TiledClutIndex(i) : i;
            words[Address(GsFormat.CT32, cbp, 1, stored % width, stored / width) + csa * 8] = colours[i];
        }
    }

    public void ReadIndices(GsFormat format, int bp, int bufferWidth, int width, int height, Span<byte> destination)
    {
        int ppr = CheckedPagesPerRow(format, bp, bufferWidth, width, height);
        int i = 0;
        if (format.UnitsPerWord == 4)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    destination[i++] = _memory[Address(format, bp, ppr, x, y)];
        }
        else
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int address = Address(format, bp, ppr, x, y);
                    destination[i++] = (byte)((_memory[address >> 1] >> ((address & 1) * 4)) & 0xF);
                }
            }
        }
    }
}
