using System.Buffers.Binary;
using System.Numerics;

namespace GTTexEdit.Core.Gs;

/// <summary>A set of GS memory nibbles (8 per word, 512 per block): the finest unit any storage mode addresses.</summary>
internal sealed class NibbleSet
{
    public const int NibblesPerBlock = GsFormat.BlockWords * 8;

    private readonly ulong[] _bits;

    public NibbleSet(long nibbles) => _bits = new ulong[(nibbles + 63) / 64];

    public long Capacity => (long)_bits.Length * 64;

    public void Add(long start, int count)
    {
        for (long n = start; n < start + count; n++)
        {
            if ((ulong)n < (ulong)Capacity)
                _bits[n >> 6] |= 1UL << (int)(n & 63);
        }
    }

    public bool Contains(long nibble) => (ulong)nibble < (ulong)Capacity && (_bits[nibble >> 6] >> (int)(nibble & 63) & 1) != 0;

    public long Count
    {
        get
        {
            long count = 0;
            foreach (ulong word in _bits)
                count += BitOperations.PopCount(word);
            return count;
        }
    }

    public long IntersectCount(NibbleSet other)
    {
        long count = 0;
        int length = Math.Min(_bits.Length, other._bits.Length);
        for (int i = 0; i < length; i++)
            count += BitOperations.PopCount(_bits[i] & other._bits[i]);
        return count;
    }

    public void UnionWith(NibbleSet other)
    {
        int length = Math.Min(_bits.Length, other._bits.Length);
        for (int i = 0; i < length; i++)
            _bits[i] |= other._bits[i];
    }

    /// <summary>Nibbles of block <paramref name="block"/> that are in the set (0..512).</summary>
    public int CountInBlock(int block)
    {
        int count = 0;
        int first = block * (NibblesPerBlock / 64);
        for (int i = first; i < first + NibblesPerBlock / 64 && i < _bits.Length; i++)
            count += BitOperations.PopCount(_bits[i]);
        return count;
    }

    /// <summary>First and last block the set touches, or (-1, -1) when it is empty.</summary>
    public (int First, int Last) BlockRange()
    {
        int first = Array.FindIndex(_bits, w => w != 0);
        if (first < 0)
            return (-1, -1);
        int last = Array.FindLastIndex(_bits, w => w != 0);
        return (first / (NibblesPerBlock / 64), last / (NibblesPerBlock / 64));
    }
}

internal enum OverlapKind
{
    /// <summary>Both textures read exactly the same nibbles.</summary>
    Same,
    /// <summary>Every nibble of B is also read by A (B is a window into A).</summary>
    AContainsB,
    BContainsA,
    Partial,
}

/// <summary>
/// Everything there is to know about one Tex1 set, for debugging and statistics: the raw tables, which GS nibbles
/// every texture / CLUT / mip level reads, who shares memory with whom, what nobody reads, and how the file is
/// laid out. Deliberately tolerant - a set the decoder would reject still gets as far as it can, with
/// <see cref="Problems"/> saying what was odd.
/// </summary>
internal sealed class Tex1Analysis
{
    public sealed class Texture
    {
        public required int Index { get; init; }
        public required PgluTexture Registers { get; init; }
        public GsFormat? Layout { get; init; }
        public int PaletteSize { get; init; }

        /// <summary>The authored image: MINU..MAXU x MINV..MAXV (the real size, recorded whatever the wrap mode); the full 2^TW x 2^TH only when MAX is 0 on a REPEAT axis.</summary>
        public int VisibleX, VisibleY, VisibleWidth, VisibleHeight;
        public int FullWidth => 1 << Registers.Tw;
        public int FullHeight => 1 << Registers.Th;

        public NibbleSet Visible = null!;
        /// <summary>Nibbles of the whole 2^TW x 2^TH area; the same object as <see cref="Visible"/> when nothing is cropped.</summary>
        public NibbleSet Full = null!;
        public NibbleSet? Mips;
        public long VisibleNibbles, FullNibbles;
        public int FirstBlock, LastBlock, FullLastBlock;

        /// <summary>Indices (or texels) of the visible rectangle, row-major; one byte per texel for indexed formats.</summary>
        public byte[]? Indices;
        public int UsedIndexCount, MaxIndexUsed;
        public ulong PixelHash;
        public ClutSlot? Clut;

        /// <summary>Nibbles of the visible area that another texture with a DIFFERENT visible set also reads.</summary>
        public long SharedNibbles;
        /// <summary>Number of textures (this one included) whose visible nibble set is identical.</summary>
        public int SameBufferGroup = 1;
    }

    public sealed class ClutSlot
    {
        public required int Cbp { get; init; }
        public required int Csa { get; init; }
        public required int Size { get; init; }
        public required GsPsm Cpsm { get; init; }
        public NibbleSet Nibbles = null!;
        public uint[] Colors = [];
        /// <summary>Tex1-relative offset of the 4 bytes each entry was uploaded from (-1 = not from a PSMCT32 transfer); in index order.</summary>
        public int[] Sources = [];
        public ulong ContentHash;
        public List<int> Users = [];
        /// <summary>Users that reach this CLUT only through a clut patch set (variant > 0).</summary>
        public List<(int Texture, int Variant)> VariantUsers = [];
        public List<int> InsideVisiblePixelsOf = [];
        public List<int> InsideDeclaredAreaOf = [];
        public List<(int Cbp, int Csa)> PartiallyOverlaps = [];
    }

    public sealed record Transfer(int Index, int DataOffset, int Bp, int Bw, GsPsm Psm, int Width, int Height, int DataSize, int FirstBlock, int LastBlock);
    public sealed record ClutPatch(int Set, int Texture, int Format, int Cbp, int Csa, uint Raw);
    public sealed record Overlap(int A, int B, OverlapKind Kind, long Common);
    public sealed record FileRegion(string Name, int Start, int End);

    public readonly byte[] Data;
    public readonly List<string> Problems = [];

    // header
    public uint RelocPointer, Header08, SizeField, ClutPatchesOffset, ClutAnimationOffset, Header28, Header2C;
    public int BaseTbp, BlockCount, TextureCount, TransferCount, TexturesOffset, TransfersOffset;

    public Texture[] Textures = [];
    public Transfer[] Transfers = [];
    public List<ClutPatch> ClutPatches = [];
    public int ClutPatchSetCount;
    public List<ClutSlot> Cluts = [];
    public List<Overlap> Overlaps = [];
    public List<FileRegion> FileRegions = [];
    public List<(int Start, int End, bool AllZero)> FileGaps = [];

    public GsMemory Memory = null!;
    public int MemoryBlocks;
    /// <summary>Per GS word: Tex1-relative offset of its 4 source bytes, -1 when no PSMCT32 transfer wrote it.</summary>
    public int[] WordSources = [];

    private int[]? _nibbleSources;

    /// <summary>
    /// Per GS NIBBLE: the nibble of the set's own data it was uploaded from (byte * 2 + half), or -1.
    /// <see cref="WordSources"/> only answers while every transfer is PSMCT32, which is true of every car set but
    /// not of the sets a course archive carries - those upload in the texture's own storage mode, so a texel can
    /// be a single nibble of the file. Built on demand, because most callers never need it.
    /// </summary>
    public int[] NibbleSources()
    {
        if (_nibbleSources is not null)
            return _nibbleSources;

        var sources = new int[(int)Math.Min((long)MemoryBlocks * NibbleSet.NibblesPerBlock, int.MaxValue / 2)];
        Array.Fill(sources, -1);

        foreach (Transfer t in Transfers)
        {
            if (GsFormat.ForSwizzle(t.Psm) is not GsFormat format || t.DataOffset + t.DataSize > Data.Length)
                continue;

            int ppr = GsMemory.PagesPerRow(format, t.Bw);
            int perTexel = 8 / format.UnitsPerWord;          // nibbles one texel occupies
            long first = (long)t.DataOffset * 2;             // where the transfer's data starts, in file nibbles
            for (int y = 0, i = 0; y < t.Height; y++)
            {
                for (int x = 0; x < t.Width; x++, i++)
                {
                    long destination = (long)GsMemory.Address(format, t.Bp, ppr, x, y) * perTexel;
                    long source = first + (long)i * perTexel;
                    for (int k = 0; k < perTexel; k++)
                    {
                        if ((ulong)(destination + k) < (ulong)sources.Length)
                            sources[destination + k] = (int)(source + k);
                    }
                }
            }
        }
        return _nibbleSources = sources;
    }

    /// <summary>Tex1-relative offset of the four bytes a GS word was uploaded from, whatever carried them, or -1.</summary>
    public int ByteSourceOfWord(int word)
    {
        if (word >= 0 && word < WordSources.Length && WordSources[word] >= 0)
            return WordSources[word];
        int[] nibbles = NibbleSources();
        long at = (long)word * 8;
        int source = at >= 0 && at < nibbles.Length ? nibbles[(int)at] : -1;
        return source >= 0 && source % 2 == 0 ? source / 2 : -1;
    }
    public NibbleSet Written = null!;
    public long WordsWrittenTwice;

    // totals (nibbles)
    /// <summary>PixelNibbles: GS nibbles some texture reads. SumVisibleNibbles: the same with no sharing at all.
    /// SumDistinctBufferNibbles: with only whole-buffer sharing (same pixels, other CLUT) - the rest is overlap.</summary>
    public long PixelNibbles, SumVisibleNibbles, SumDistinctBufferNibbles, ClutNibbles, UnreadNibbles, UnreadNonZeroNibbles;
    /// <summary>Blocks if every texture had its own pixels and its own CLUT block(s): no sharing of any kind.</summary>
    public int NaiveBlocks;
    /// <summary>Blocks with identical pixel buffers merged and 16-colour CLUTs packed four to a block, but no overlapping.</summary>
    public int DedupedBlocks;

    public Tex1Analysis(byte[] data, bool keepIndices = true)
    {
        Data = data;
        ReadTables();
        ReplayTransfers();
        AnalyseTextures(keepIndices);
        AnalyseCluts();
        AnalyseSharing();
        AnalyseFileLayout();
    }

    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
    private static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);

    private void ReadTables()
    {
        ReadOnlySpan<byte> d = Data;
        if (d.Length < Tex1Reader.HeaderSize || U32(d, 0) != Tex1Reader.Magic)
            throw new InvalidDataException("Not a Tex1 texture set.");

        RelocPointer = U32(d, 0x04);
        Header08 = U32(d, 0x08);
        SizeField = U32(d, 0x0C);
        BaseTbp = U16(d, 0x10);
        BlockCount = U16(d, 0x12);
        TextureCount = U16(d, 0x14);
        TransferCount = U16(d, 0x16);
        TexturesOffset = (int)U32(d, 0x18);
        TransfersOffset = (int)U32(d, 0x1C);
        ClutPatchesOffset = U32(d, 0x20);
        ClutAnimationOffset = U32(d, 0x24);
        Header28 = U32(d, 0x28);
        Header2C = U32(d, 0x2C);

        Textures = new Texture[TextureCount];
        for (int i = 0; i < TextureCount; i++)
        {
            PgluTexture registers = PgluTexture.Read(d.Slice(TexturesOffset + i * PgluTexture.Size, PgluTexture.Size));
            GsFormat? layout = registers.Psm switch
            {
                GsPsm.PSMCT32 or GsPsm.PSMCT24 => GsFormat.CT32,
                GsPsm.PSMCT16 or GsPsm.PSMCT16S => GsFormat.CT16,
                GsPsm.PSMT8 => GsFormat.T8,
                GsPsm.PSMT4 or GsPsm.PSMT4HL or GsPsm.PSMT4HH => GsFormat.T4,
                _ => null,
            };
            if (layout is null)
                Problems.Add($"texture {i}: unsupported PSM {registers.Psm}");
            if (registers.Psm is GsPsm.PSMT4HL or GsPsm.PSMT4HH or GsPsm.PSMT8H)
                Problems.Add($"texture {i}: high-bits format {registers.Psm}");
            Textures[i] = new Texture
            {
                Index = i, Registers = registers, Layout = layout,
                PaletteSize = layout == GsFormat.T8 ? 256 : layout == GsFormat.T4 ? 16 : 0,
            };
        }

        Transfers = new Transfer[TransferCount];
        for (int i = 0; i < TransferCount; i++)
        {
            int o = TransfersOffset + i * Tex1Reader.TransferInfoSize;
            int offset = (int)U32(d, o), bp = U16(d, o + 4), bw = d[o + 6], width = U16(d, o + 8), height = U16(d, o + 10);
            var psm = (GsPsm)d[o + 7];
            GsFormat? format = GsFormat.ForSwizzle(psm);
            int size = format is null ? 0 : GsFormat.GetDataSize(width, height, psm);
            int lastBlock = format is null ? bp : (int)((GsMemory.WordsReached(format, bp, GsMemory.PagesPerRow(format, bw), width, height) - 1) / GsFormat.BlockWords);
            Transfers[i] = new Transfer(i, offset, bp, bw, psm, width, height, size, bp, lastBlock);
            if (format is null)
                Problems.Add($"transfer {i}: unsupported format {psm}");
        }

        if (ClutPatchesOffset != 0)
        {
            int at = (int)ClutPatchesOffset;
            ClutPatchSetCount = (int)U32(d, at);
            for (int set = 0; set < ClutPatchSetCount && set < 256; set++)
            {
                int setOffset = (int)U32(d, at + 4 + set * 4);
                int count = (int)U32(d, setOffset);
                for (int i = 0; i < count; i++)
                {
                    uint raw = U32(d, setOffset + 4 + i * 4);
                    ClutPatches.Add(new ClutPatch(set, (int)(raw >> 23), (int)(raw >> 19 & 0xF), (int)(raw >> 5 & 0x3FFF), (int)(raw & 0x1F), raw));
                }
            }
        }
    }

    private void ReplayTransfers()
    {
        long words = (long)BlockCount * GsFormat.BlockWords;
        foreach (Transfer t in Transfers)
            words = Math.Max(words, (long)(t.LastBlock + 1) * GsFormat.BlockWords);
        foreach (Texture t in Textures)
        {
            if (t.Layout is GsFormat layout)
                words = Math.Max(words, GsMemory.WordsReached(layout, t.Registers.Tbp0, GsMemory.PagesPerRow(layout, t.Registers.Tbw), t.FullWidth, t.FullHeight));
        }
        words = Math.Min(words, (long)GsFormat.MaxBlocks * GsFormat.BlockWords);

        Memory = new GsMemory(words);
        MemoryBlocks = (int)(words / GsFormat.BlockWords);
        WordSources = new int[words];
        Array.Fill(WordSources, -1);
        Written = new NibbleSet(words * 8);
        var writeCount = new byte[words];

        foreach (Transfer t in Transfers)
        {
            if (GsFormat.ForSwizzle(t.Psm) is not GsFormat format)
                continue;
            if (t.DataOffset + t.DataSize > Data.Length)
            {
                Problems.Add($"transfer {t.Index}: data runs past the end of the set");
                continue;
            }
            Memory.Write(format, t.Bp, t.Bw, t.Width, t.Height, Data.AsSpan(t.DataOffset, t.DataSize));

            int ppr = GsMemory.PagesPerRow(format, t.Bw);
            int nibblesPerUnit = 8 / format.UnitsPerWord;
            for (int y = 0, i = 0; y < t.Height; y++)
            {
                for (int x = 0; x < t.Width; x++, i++)
                {
                    int address = GsMemory.Address(format, t.Bp, ppr, x, y);
                    Written.Add((long)address * nibblesPerUnit, nibblesPerUnit);
                    int word = address / format.UnitsPerWord;
                    if ((uint)word < (uint)WordSources.Length)
                    {
                        WordSources[word] = format == GsFormat.CT32 ? t.DataOffset + i * 4 : -1;
                        if (format == GsFormat.CT32 && writeCount[word] < 255)
                            writeCount[word]++;
                    }
                }
            }
        }
        WordsWrittenTwice = writeCount.Count(c => c > 1);
    }

    private NibbleSet ReadSet(GsFormat layout, int bp, int bw, int x0, int y0, int width, int height, int wordOffset = 0)
    {
        var set = new NibbleSet((long)MemoryBlocks * NibbleSet.NibblesPerBlock);
        int ppr = GsMemory.PagesPerRow(layout, bw);
        int nibblesPerUnit = 8 / layout.UnitsPerWord;
        for (int y = y0; y < y0 + height; y++)
            for (int x = x0; x < x0 + width; x++)
                set.Add(((long)GsMemory.Address(layout, bp, ppr, x, y) + (long)wordOffset * layout.UnitsPerWord) * nibblesPerUnit, nibblesPerUnit);
        return set;
    }

    private static (int Start, int Length) VisibleAxis(int mode, int min, int max, int full)
    {
        switch (mode)
        {
            case PgluTexture.RegionClamp:
            {
                int start = Math.Min(min, full - 1);
                return (start, Math.Clamp(max + 1, start + 1, full) - start);
            }
            case 3: // REGION_REPEAT: MIN is the mask, MAX the fixed bits
            {
                int start = Math.Min(max, full - 1);
                return (start, Math.Clamp(min + 1, 1, full - start));
            }
            default:
                // Car sets record the real image size in MAXU / MAXV on REPEAT axes too (where the GS ignores them):
                // 2^TW is the next power of two of MAXU + 1 in all 177,353 car textures, and the memory right of /
                // below it belongs to other textures. 0 means "not recorded" (menu GPB sets): the full size.
                return (0, max == 0 ? full : Math.Clamp(max + 1, 1, full));
        }
    }

    private void AnalyseTextures(bool keepIndices)
    {
        byte[] memory = Memory.Raw;
        foreach (Texture t in Textures)
        {
            PgluTexture r = t.Registers;
            (t.VisibleX, t.VisibleWidth) = VisibleAxis(r.Wms, r.MinU, r.MaxU, t.FullWidth);
            (t.VisibleY, t.VisibleHeight) = VisibleAxis(r.Wmt, r.MinV, r.MaxV, t.FullHeight);
            if (r.Wms == 3 || r.Wmt == 3)
                Problems.Add($"texture {t.Index}: REGION_REPEAT wrap mode");
            if (r.Wms == PgluTexture.RegionClamp && r.MinU != 0 || r.Wmt == PgluTexture.RegionClamp && r.MinV != 0)
                Problems.Add($"texture {t.Index}: clamp region does not start at 0 (MINU {r.MinU}, MINV {r.MinV})");

            if (t.Layout is not GsFormat layout)
            {
                t.Visible = t.Full = new NibbleSet(0);
                t.FirstBlock = t.LastBlock = t.FullLastBlock = -1;
                continue;
            }
            if ((layout == GsFormat.T4 || layout == GsFormat.T8) && (r.Tbw & 1) != 0)
                Problems.Add($"texture {t.Index}: odd TBW {r.Tbw} on an indexed texture");

            t.Visible = ReadSet(layout, r.Tbp0, r.Tbw, t.VisibleX, t.VisibleY, t.VisibleWidth, t.VisibleHeight);
            bool cropped = t.VisibleWidth != t.FullWidth || t.VisibleHeight != t.FullHeight;
            t.Full = cropped ? ReadSet(layout, r.Tbp0, r.Tbw, 0, 0, t.FullWidth, t.FullHeight) : t.Visible;
            t.VisibleNibbles = t.Visible.Count;
            t.FullNibbles = cropped ? t.Full.Count : t.VisibleNibbles;
            (t.FirstBlock, t.LastBlock) = t.Visible.BlockRange();
            t.FullLastBlock = t.Full.BlockRange().Last;

            for (int level = 1; level <= r.Mxl && level <= 6; level++)
            {
                var (tbp, tbw) = r.GetMip(level);
                int w = Math.Max(1, t.FullWidth >> level), h = Math.Max(1, t.FullHeight >> level);
                t.Mips ??= new NibbleSet((long)MemoryBlocks * NibbleSet.NibblesPerBlock);
                NibbleSet mip = ReadSet(layout, tbp, tbw, 0, 0, w, h); // nibbles past the set's memory are dropped
                if (mip.Count < (long)w * h * 8 / layout.UnitsPerWord)
                    Problems.Add($"texture {t.Index}: mip level {level} lies outside the set");
                t.Mips.UnionWith(mip);
            }

            // texels of the visible rectangle
            int ppr = GsMemory.PagesPerRow(layout, r.Tbw);
            int bytesPerTexel = layout == GsFormat.CT32 ? 4 : layout == GsFormat.CT16 ? 2 : 1;
            var texels = new byte[t.VisibleWidth * t.VisibleHeight * bytesPerTexel];
            var used = new bool[256];
            for (int y = 0, i = 0; y < t.VisibleHeight; y++)
            {
                for (int x = 0; x < t.VisibleWidth; x++)
                {
                    int address = GsMemory.Address(layout, r.Tbp0, ppr, x + t.VisibleX, y + t.VisibleY);
                    switch (bytesPerTexel)
                    {
                        case 4:
                            memory.AsSpan(address * 4, 4).CopyTo(texels.AsSpan(i));
                            i += 4;
                            break;
                        case 2:
                            memory.AsSpan(address * 2, 2).CopyTo(texels.AsSpan(i));
                            i += 2;
                            break;
                        default:
                            byte index = layout == GsFormat.T8 ? memory[address] : (byte)(memory[address >> 1] >> ((address & 1) * 4) & 0xF);
                            texels[i++] = index;
                            used[index] = true;
                            break;
                    }
                }
            }
            if (bytesPerTexel == 1)
            {
                for (int i = 0; i < 256; i++)
                {
                    if (used[i])
                    {
                        t.UsedIndexCount++;
                        t.MaxIndexUsed = i;
                    }
                }
            }
            t.PixelHash = Hash(texels, (ulong)(t.VisibleWidth * 2654435761L + t.VisibleHeight * 40503L + layout.UnitsPerWord));
            if (keepIndices)
                t.Indices = texels;
        }
    }

    /// <summary>
    /// Where one entry of a 16-BIT palette really sits. CSA counts 32-byte slots, and a 16-colour palette of
    /// 16-bit entries is exactly one of them - which is why eight of them, at CSA 0 to 7, fill a block precisely,
    /// and why a 16-colour palette of 32-bit entries takes two slots and so only ever uses an even CSA.
    ///
    /// Reading it as an 8x2 area of 16-bit pixels - which is what the area is, and what this and PDTools both used
    /// to do - spreads those sixteen entries across 64 bytes instead, taking every other halfword. That leaves half
    /// of a palette-only block read by nothing (6,589 non-zero bytes across the 290 GT3 cars), makes every palette
    /// in such a block appear to overlap its neighbours, and renders textures that use one as blank transparency.
    /// Halving the offset within the block is what the file itself says: eight palettes, 32 bytes each, no gaps.
    /// </summary>
    private static int HalfOfClut(int cbp, int address, int csa)
    {
        int blockStart = cbp * GsFormat.BlockWords * 2;      // the block, in halfwords
        return blockStart + (address - blockStart) / 2 + csa * 16;
    }

    private ClutSlot GetClut(int cbp, int csa, int size, GsPsm cpsm)
    {
        ClutSlot? slot = Cluts.Find(c => c.Cbp == cbp && c.Csa == csa && c.Size == size && c.Cpsm == cpsm);
        if (slot is not null)
            return slot;

        slot = new ClutSlot { Cbp = cbp, Csa = csa, Size = size, Cpsm = cpsm };
        int width = size == 256 ? 16 : 8, height = size == 256 ? 16 : 2;
        GsFormat layout = cpsm == GsPsm.PSMCT16 || cpsm == GsPsm.PSMCT16S ? GsFormat.CT16 : GsFormat.CT32;
        if (cpsm != GsPsm.PSMCT32 && layout == GsFormat.CT32)
            Problems.Add($"CLUT {cbp}/{csa}: unexpected CPSM {cpsm}");

        // exact reach (WordsReached rounds up to whole pages, far too much for a CLUT in the set's last blocks)
        long reach = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                reach = Math.Max(reach, GsMemory.Address(layout, cbp, 1, x, y) / layout.UnitsPerWord + csa * 8 + 1);
        if (reach > (long)MemoryBlocks * GsFormat.BlockWords)
        {
            Problems.Add($"CLUT {cbp}/{csa}: lies outside the set's memory");
            slot.Nibbles = new NibbleSet(0);
            Cluts.Add(slot);
            return slot;
        }

        slot.Nibbles = layout == GsFormat.CT32
            ? ReadSet(layout, cbp, 1, 0, 0, width, height, wordOffset: csa * 8)
            : new NibbleSet((long)MemoryBlocks * NibbleSet.NibblesPerBlock);
        slot.Colors = new uint[size];
        slot.Sources = new int[size];
        byte[] memory = Memory.Raw;
        for (int i = 0; i < size; i++)
        {
            int stored = size == 256 ? Tex1Reader.TiledClutIndex(i) : i;
            int address = GsMemory.Address(layout, cbp, 1, stored % width, stored / width);
            if (layout == GsFormat.CT32)
            {
                int word = address + csa * 8;
                slot.Colors[i] = BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan(word * 4));
                slot.Sources[i] = ByteSourceOfWord(word);
            }
            else
            {
                int half = HalfOfClut(cbp, address, csa);
                slot.Colors[i] = BinaryPrimitives.ReadUInt16LittleEndian(memory.AsSpan(half * 2));
                slot.Sources[i] = -1;
                slot.Nibbles.Add((long)half * 4, 4);
            }
        }
        slot.ContentHash = Hash(System.Runtime.InteropServices.MemoryMarshal.AsBytes(slot.Colors.AsSpan()), (ulong)size);
        Cluts.Add(slot);
        return slot;
    }

    /// <summary>
    /// Where texture <paramref name="texture"/> takes its palette from in one VARIATION. GT3 switches a car's
    /// colour this way: every clut patch set points the textures at a different CLUT of the same Tex1, which is
    /// what a GT4 colour patch achieves by rewriting the CLUT words in place.
    /// </summary>
    public (int Cbp, int Csa) ClutPointer(int texture, int variation)
    {
        ClutPatch? patch = ClutPatches.Find(p => p.Set == variation && p.Texture == texture)
                        ?? (variation > 0 ? ClutPatches.Find(p => p.Set == 0 && p.Texture == texture) : null);
        return patch is not null
            ? (patch.Cbp, patch.Csa)
            : (Textures[texture].Registers.Cbp, Textures[texture].Registers.Csa);
    }

    /// <summary>The CLUT a texture reads in one variation, with the file offsets of its entries.</summary>
    public ClutSlot? ClutFor(int texture, int variation)
    {
        if (Textures[texture].PaletteSize == 0)
            return null;
        var (cbp, csa) = ClutPointer(texture, variation);
        return Cluts.Find(c => c.Cbp == cbp && c.Csa == csa && c.Size == Textures[texture].PaletteSize)
            ?? GetClut(cbp, csa, Textures[texture].PaletteSize, Textures[texture].Registers.Cpsm);
    }

    private void AnalyseCluts()
    {
        foreach (Texture t in Textures)
        {
            if (t.PaletteSize == 0)
                continue;

            // Clut patch set 0 is the base look and overrides tex0's own pointer (see Tex1Reader).
            ClutPatch? basePatch = ClutPatches.Find(p => p.Set == 0 && p.Texture == t.Index);
            t.Clut = basePatch is null
                ? GetClut(t.Registers.Cbp, t.Registers.Csa, t.PaletteSize, t.Registers.Cpsm)
                : GetClut(basePatch.Cbp, basePatch.Csa, t.PaletteSize, t.Registers.Cpsm);
            t.Clut.Users.Add(t.Index);
            if (basePatch is not null && (basePatch.Cbp != t.Registers.Cbp || basePatch.Csa != t.Registers.Csa))
                Problems.Add($"texture {t.Index}: clut patch set 0 points at {basePatch.Cbp}/{basePatch.Csa}, tex0 at {t.Registers.Cbp}/{t.Registers.Csa}");
        }

        foreach (ClutPatch patch in ClutPatches)
        {
            if (patch.Set == 0 || patch.Texture >= Textures.Length || Textures[patch.Texture].PaletteSize == 0)
                continue;
            Texture t = Textures[patch.Texture];
            ClutSlot slot = GetClut(patch.Cbp, patch.Csa, t.PaletteSize, t.Registers.Cpsm);
            if (slot != t.Clut)
                slot.VariantUsers.Add((t.Index, patch.Set));
        }

        foreach (ClutSlot slot in Cluts)
        {
            var (first, last) = slot.Nibbles.BlockRange();
            if (first < 0)
                continue;
            foreach (Texture t in Textures)
            {
                if (t.FirstBlock < 0 || t.FullLastBlock < first || t.FirstBlock > last)
                    continue;
                if (slot.Nibbles.IntersectCount(t.Visible) > 0)
                    slot.InsideVisiblePixelsOf.Add(t.Index);
                else if (slot.Nibbles.IntersectCount(t.Full) > 0)
                    slot.InsideDeclaredAreaOf.Add(t.Index);
            }
            foreach (ClutSlot other in Cluts)
            {
                if (other != slot && other.Nibbles.IntersectCount(slot.Nibbles) > 0)
                    slot.PartiallyOverlaps.Add((other.Cbp, other.Csa));
            }
        }
    }

    private void AnalyseSharing()
    {
        long capacity = (long)MemoryBlocks * NibbleSet.NibblesPerBlock;
        var pixels = new NibbleSet(capacity);
        var everything = new NibbleSet(capacity);

        for (int a = 0; a < Textures.Length; a++)
        {
            Texture ta = Textures[a];
            if (ta.FirstBlock < 0)
                continue;
            var sharedWithOthers = new NibbleSet(capacity);
            for (int b = 0; b < Textures.Length; b++)
            {
                Texture tb = Textures[b];
                if (b == a || tb.FirstBlock < 0 || tb.LastBlock < ta.FirstBlock || tb.FirstBlock > ta.LastBlock)
                    continue;
                long common = ta.Visible.IntersectCount(tb.Visible);
                if (common == 0)
                    continue;

                bool same = common == ta.VisibleNibbles && common == tb.VisibleNibbles;
                if (same)
                    ta.SameBufferGroup++;
                else
                    sharedWithOthers.UnionWith(tb.Visible);

                if (a < b)
                {
                    OverlapKind kind = same ? OverlapKind.Same
                        : common == tb.VisibleNibbles ? OverlapKind.AContainsB
                        : common == ta.VisibleNibbles ? OverlapKind.BContainsA
                        : OverlapKind.Partial;
                    Overlaps.Add(new Overlap(a, b, kind, common));
                }
            }
            ta.SharedNibbles = sharedWithOthers.IntersectCount(ta.Visible);
            pixels.UnionWith(ta.Visible);
            if (ta.Mips is not null)
                pixels.UnionWith(ta.Mips);
        }

        PixelNibbles = pixels.Count;
        SumVisibleNibbles = Textures.Sum(t => t.VisibleNibbles);
        SumDistinctBufferNibbles = Textures.Where(t => t.FirstBlock >= 0)
            .GroupBy(t => (t.Registers.Tbp0, t.Registers.Tbw, t.Layout, t.VisibleX, t.VisibleY, t.VisibleWidth, t.VisibleHeight))
            .Sum(g => g.First().VisibleNibbles);
        everything.UnionWith(pixels);
        var cluts = new NibbleSet(capacity);
        foreach (ClutSlot slot in Cluts)
            cluts.UnionWith(slot.Nibbles);
        ClutNibbles = cluts.Count;
        everything.UnionWith(cluts);

        byte[] memory = Memory.Raw;
        long total = Math.Min(capacity, (long)BlockCount * NibbleSet.NibblesPerBlock);
        for (long n = 0; n < total; n++)
        {
            if (everything.Contains(n))
                continue;
            UnreadNibbles++;
            if ((memory[n >> 1] >> (int)((n & 1) * 4) & 0xF) != 0)
                UnreadNonZeroNibbles++;
        }

        // baselines
        var buffers = new HashSet<(int, int, GsPsm, int, int)>();
        foreach (Texture t in Textures)
        {
            if (t.Layout is not GsFormat layout)
                continue;
            int blocks = layout.GetLastBlockIndex(t.VisibleX + t.VisibleWidth, t.VisibleY + t.VisibleHeight) + 1;
            int clutBlocks = t.PaletteSize == 256 ? 4 : t.PaletteSize == 16 ? 1 : 0;
            NaiveBlocks += blocks + clutBlocks;
            if (buffers.Add((t.Registers.Tbp0, t.Registers.Tbw, t.Registers.Psm, t.VisibleWidth, t.VisibleHeight)))
                DedupedBlocks += blocks;
        }
        int small = Cluts.Count(c => c.Size == 16), large = Cluts.Count(c => c.Size == 256);
        DedupedBlocks += (small + 3) / 4 + large * 4;
    }

    private void AnalyseFileLayout()
    {
        FileRegions.Add(new FileRegion("header", 0, Tex1Reader.HeaderSize));
        if (TextureCount > 0)
            FileRegions.Add(new FileRegion("textures", TexturesOffset, TexturesOffset + TextureCount * PgluTexture.Size));
        if (TransferCount > 0)
            FileRegions.Add(new FileRegion("transfers", TransfersOffset, TransfersOffset + TransferCount * Tex1Reader.TransferInfoSize));
        foreach (Transfer t in Transfers)
            FileRegions.Add(new FileRegion($"data of transfer {t.Index}", t.DataOffset, t.DataOffset + t.DataSize));
        if (ClutPatchesOffset != 0)
        {
            int at = (int)ClutPatchesOffset;
            FileRegions.Add(new FileRegion("clut patch table", at, at + 4 + ClutPatchSetCount * 4));
            for (int set = 0; set < ClutPatchSetCount && set < 256; set++)
            {
                int setOffset = (int)U32(Data, at + 4 + set * 4);
                FileRegions.Add(new FileRegion($"clut patch set {set}", setOffset, setOffset + 4 + (int)U32(Data, setOffset) * 4));
            }
        }
        FileRegions.Sort((x, y) => x.Start.CompareTo(y.Start));

        int position = 0;
        foreach (FileRegion region in FileRegions)
        {
            if (region.Start > position)
                FileGaps.Add((position, region.Start, IsZero(position, region.Start)));
            else if (region.Start < position)
                Problems.Add($"file region '{region.Name}' overlaps the one before it");
            position = Math.Max(position, region.End);
        }
        // The length a set states is its content rounded up to 0x10, and some of the originals pad further than
        // that - which is theirs to do. Stating LESS than it holds is the error worth reporting.
        if (SizeField < (uint)position)
            Problems.Add($"size field 0x{SizeField:x} but the last region ends at 0x{position:x}");
    }

    private bool IsZero(int start, int end)
    {
        for (int i = start; i < end && i < Data.Length; i++)
        {
            if (Data[i] != 0)
                return false;
        }
        return true;
    }

    /// <summary>Who reads block <paramref name="block"/>, as a compact description ("px t10,t14 | clut 265/0 (t10)").</summary>
    public string DescribeBlock(int block)
    {
        var parts = new List<string>();
        var px = Textures.Where(t => t.FirstBlock >= 0 && block >= t.FirstBlock && block <= t.LastBlock && t.Visible.CountInBlock(block) > 0).Select(t => $"t{t.Index}").ToList();
        if (px.Count > 0)
            parts.Add("px " + string.Join(",", px));
        var declared = Textures.Where(t => t.FirstBlock >= 0 && t.Full != t.Visible && t.Full.CountInBlock(block) > 0 && t.Visible.CountInBlock(block) == 0).Select(t => $"t{t.Index}").ToList();
        if (declared.Count > 0)
            parts.Add("declared-only " + string.Join(",", declared));
        var mips = Textures.Where(t => t.Mips is not null && t.Mips.CountInBlock(block) > 0).Select(t => $"t{t.Index}").ToList();
        if (mips.Count > 0)
            parts.Add("mip " + string.Join(",", mips));
        foreach (ClutSlot slot in Cluts.Where(c => c.Nibbles.CountInBlock(block) > 0))
        {
            string users = string.Join(",", slot.Users.Select(u => $"t{u}").Concat(slot.VariantUsers.Select(v => $"t{v.Texture}v{v.Variant}")));
            parts.Add($"clut{slot.Size} {slot.Cbp}/{slot.Csa} ({users})");
        }
        return parts.Count == 0 ? "-" : string.Join(" | ", parts);
    }

    public bool IsBlockZero(int block)
    {
        ReadOnlySpan<byte> bytes = Memory.Raw.AsSpan(block * GsFormat.BlockBytes, GsFormat.BlockBytes);
        return bytes.IndexOfAnyExcept((byte)0) < 0;
    }

    public ulong HashBlock(int block) => Hash(Memory.Raw.AsSpan(block * GsFormat.BlockBytes, GsFormat.BlockBytes), 0);

    internal static ulong Hash(ReadOnlySpan<byte> data, ulong seed)
    {
        ulong hash = 14695981039346656037UL ^ seed;
        foreach (byte b in data)
            hash = (hash ^ b) * 1099511628211UL;
        return hash;
    }
}
