namespace GTTexEdit.Core.Gs;

/// <summary>GS pixel storage modes (SCE_GS_PSM*), as found in tex0.PSM, tex0.CPSM and a transfer's format byte.</summary>
internal enum GsPsm : byte
{
    PSMCT32 = 0,   // RGBA32
    PSMCT24 = 1,   // RGB24, upper 8 bits unused (the GS substitutes TEXA for alpha)
    PSMCT16 = 2,   // RGBA5551, two pixels per 32-bit word
    PSMCT16S = 10,
    PSMT8 = 19,    // 8-bit indexed, four pixels per word
    PSMT4 = 20,    // 4-bit indexed, eight pixels per word
    PSMT8H = 27,
    PSMT4HL = 36,
    PSMT4HH = 44,
    PSMZ32 = 48,
    PSMZ24 = 49,
    PSMZ16 = 50,
    PSMZ16S = 58,
}

/// <summary>
/// How one pixel storage mode is laid out in GS local memory. The GS does not store a texture linearly: memory
/// is cut into 8 KB pages of 32 blocks (256 bytes = 64 words each), blocks are ordered along a Z curve inside
/// the page, and the words inside a block are interleaved again ("swizzled"). A block therefore covers a
/// different pixel rectangle per format: 8x8 for PSMCT32, 16x8 for PSMCT16, 16x16 for PSMT8, 32x16 for PSMT4.
/// See the GS User's Manual p.161-175. Everything here is immutable, so the shared instances are thread-safe.
/// </summary>
internal sealed class GsFormat
{
    public const int BlocksPerPage = 32;
    public const int BlockWords = 64;
    public const int BlockBytes = BlockWords * 4;
    public const int PageWords = BlockWords * BlocksPerPage;

    /// <summary>4 MB of GS local memory.</summary>
    public const int MaxBlocks = 16384;

    // (Declared before the instances below: their constructors use it.)
    // Word order of the 8 pixels of one pixel row inside a column (a column is 16 words). Rows alternate A, B;
    // the 4-row columns of the indexed formats continue with C, D, and every odd column swaps the two pairs.
    private static readonly int[][] ColumnWordRows =
    [
        [ 0,  1,  4,  5,  8,  9, 12, 13],
        [ 2,  3,  6,  7, 10, 11, 14, 15],
        [ 8,  9, 12, 13,  0,  1,  4,  5],
        [10, 11, 14, 15,  2,  3,  6,  7],
    ];

    public static readonly GsFormat CT32 = new(GsPsm.PSMCT32, pageWidth: 64, pageHeight: 32, blockWidth: 8, blockHeight: 8, columnHeight: 2, unitsPerWord: 1,
    [
         0,  1,  4,  5, 16, 17, 20, 21,
         2,  3,  6,  7, 18, 19, 22, 23,
         8,  9, 12, 13, 24, 25, 28, 29,
        10, 11, 14, 15, 26, 27, 30, 31,
    ]);

    public static readonly GsFormat CT16 = new(GsPsm.PSMCT16, pageWidth: 64, pageHeight: 64, blockWidth: 16, blockHeight: 8, columnHeight: 2, unitsPerWord: 2,
    [
         0,  2,  8, 10,
         1,  3,  9, 11,
         4,  6, 12, 14,
         5,  7, 13, 15,
        16, 18, 24, 26,
        17, 19, 25, 27,
        20, 22, 28, 30,
        21, 23, 29, 31,
    ]);

    public static readonly GsFormat T8 = new(GsPsm.PSMT8, pageWidth: 128, pageHeight: 64, blockWidth: 16, blockHeight: 16, columnHeight: 4, unitsPerWord: 4,
    [
         0,  1,  4,  5, 16, 17, 20, 21,
         2,  3,  6,  7, 18, 19, 22, 23,
         8,  9, 12, 13, 24, 25, 28, 29,
        10, 11, 14, 15, 26, 27, 30, 31,
    ]);

    public static readonly GsFormat T4 = new(GsPsm.PSMT4, pageWidth: 128, pageHeight: 128, blockWidth: 32, blockHeight: 16, columnHeight: 4, unitsPerWord: 8,
    [
         0,  2,  8, 10,
         1,  3,  9, 11,
         4,  6, 12, 14,
         5,  7, 13, 15,
        16, 18, 24, 26,
        17, 19, 25, 27,
        20, 22, 28, 30,
        21, 23, 29, 31,
    ]);

    public readonly GsPsm Psm;
    public readonly int PageWidth, PageHeight;
    public readonly int BlockWidth, BlockHeight;
    public readonly int PageColumns;

    /// <summary>Pixels per 32-bit word: the unit <see cref="PageTable"/> addresses (word, half, byte or nibble).</summary>
    public readonly int UnitsPerWord;

    /// <summary>Block number inside a page for each block cell, row-major (<see cref="PageColumns"/> per row).</summary>
    public readonly int[] BlockLayout;

    /// <summary>
    /// Address, in <see cref="UnitsPerWord"/> units from the start of the page, of page pixel (px, py) at index
    /// [(py &lt;&lt; PageShiftX) | px]. Folds the block layout and the in-block word interleave into one lookup.
    /// </summary>
    public readonly ushort[] PageTable;

    public readonly int PageShiftX, PageShiftY;

    // Cell (column, row) of each of a page's 32 blocks: the inverse of BlockLayout.
    private readonly int[] _blockCellX = new int[BlocksPerPage];
    private readonly int[] _blockCellY = new int[BlocksPerPage];

    private GsFormat(GsPsm psm, int pageWidth, int pageHeight, int blockWidth, int blockHeight, int columnHeight, int unitsPerWord, int[] blockLayout)
    {
        Psm = psm;
        PageWidth = pageWidth;
        PageHeight = pageHeight;
        BlockWidth = blockWidth;
        BlockHeight = blockHeight;
        PageColumns = pageWidth / blockWidth;
        UnitsPerWord = unitsPerWord;
        BlockLayout = blockLayout;
        PageShiftX = int.Log2(pageWidth);
        PageShiftY = int.Log2(pageHeight);

        for (int cell = 0; cell < blockLayout.Length; cell++)
        {
            _blockCellX[blockLayout[cell]] = cell % PageColumns;
            _blockCellY[blockLayout[cell]] = cell / PageColumns;
        }

        PageTable = new ushort[pageWidth * pageHeight];
        for (int py = 0; py < pageHeight; py++)
        {
            for (int px = 0; px < pageWidth; px++)
            {
                int blockX = px / blockWidth;
                int blockY = py / blockHeight;
                int block = blockLayout[blockX + blockY * PageColumns];

                int cx = px - blockX * blockWidth;
                int by = py - blockY * blockHeight;
                int column = by / columnHeight;
                int cy = by - column * columnHeight;

                int[] wordRow = ColumnWordRows[columnHeight == 4 && (column & 1) != 0 ? (cy + 2) & 3 : cy];
                int word = block * BlockWords + column * 16 + wordRow[cx & 7];

                // Position inside the word: a block row is 8 pixels per word-sized step, so pixels 8.. of the
                // wider blocks land in the next half / byte / nibble pair; rows 2-3 of a 4-row column take the odd ones.
                int sub = unitsPerWord switch
                {
                    1 => 0,
                    2 => cx >> 3,
                    _ => (cx >> 3) * 2 + (cy >> 1),
                };

                PageTable[py * pageWidth + px] = (ushort)(word * unitsPerWord + sub);
            }
        }
    }

    /// <summary>The layout a transfer or texture of storage mode <paramref name="psm"/> is swizzled with, or null when unsupported.</summary>
    public static GsFormat? ForSwizzle(GsPsm psm) => psm switch
    {
        GsPsm.PSMCT32 => CT32,
        GsPsm.PSMCT16 => CT16,
        GsPsm.PSMT8 => T8,
        GsPsm.PSMT4 or GsPsm.PSMT4HL or GsPsm.PSMT4HH => T4, // GT2K sets use the HL/HH modes; stored like PSMT4
        _ => null,
    };

    /// <summary>Byte size of <paramref name="width"/> x <paramref name="height"/> pixels of transfer data.</summary>
    public static int GetDataSize(int width, int height, GsPsm psm)
    {
        int bpp = psm switch
        {
            GsPsm.PSMCT32 => 32,
            GsPsm.PSMCT16 => 16,
            GsPsm.PSMT8 => 8,
            GsPsm.PSMT4 or GsPsm.PSMT4HL or GsPsm.PSMT4HH => 4,
            _ => throw new NotSupportedException($"Tex1 transfer format {psm} is not supported."),
        };
        return (int)(((long)width * height * bpp + 7) / 8); // an odd PSMT4 pixel count still takes its last byte
    }

    public int GetNumPagesPerRow(int width) => Math.Max(1, (width + PageWidth - 1) / PageWidth);

    /// <summary>Index of the GS block holding the bottom-right pixel of a <paramref name="width"/> x <paramref name="height"/> image based at block 0.</summary>
    public int GetLastBlockIndex(int width, int height)
    {
        if (width == 0 && height == 0)
            return 0;

        int x = Math.Max(0, width - 1);
        int y = Math.Max(0, height - 1);

        // The old builder derives the page row length from the LAST PIXEL's x, not from the width, so a width of
        // n * PageWidth + 1 counts one page per row too few. Kept: the block count ends up in the file.
        int pagesPerRow = GetNumPagesPerRow(x);

        int pageX = x / PageWidth;
        int pageY = y / PageHeight;
        int page = pageX + pageY * pagesPerRow;

        int blockX = (x - pageX * PageWidth) / BlockWidth;
        int blockY = (y - pageY * PageHeight) / BlockHeight;
        return page * BlocksPerPage + BlockLayout[blockX + blockY * PageColumns];
    }

    /// <summary>Top-left pixel covered by block <paramref name="blockIndex"/> of an image <paramref name="pagesPerRow"/> pages wide.</summary>
    public (int X, int Y) GetPositionOfBlock(int blockIndex, int pagesPerRow)
    {
        int page = blockIndex / BlocksPerPage;
        int block = blockIndex % BlocksPerPage;
        return (page % pagesPerRow * PageWidth + _blockCellX[block] * BlockWidth,
                page / pagesPerRow * PageHeight + _blockCellY[block] * BlockHeight);
    }

    /// <summary>
    /// For each block 0..last of a <paramref name="width"/> x <paramref name="height"/> image: true when the image
    /// puts pixels in it, false when the block is only allocated because of the Z-curve order (free for a CLUT).
    /// </summary>
    public bool[] GetBlockUsage(int width, int height)
    {
        var used = new bool[GetLastBlockIndex(width, height) + 1];
        int pagesPerRow = GetNumPagesPerRow(width);
        for (int i = 0; i < used.Length; i++)
        {
            var (x, y) = GetPositionOfBlock(i, pagesPerRow);
            used[i] = x < width && y < height;
        }
        return used;
    }
}
