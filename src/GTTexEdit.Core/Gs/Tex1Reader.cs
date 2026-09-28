using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace GTTexEdit.Core.Gs;

/* Tex1 (TextureSet1, GT3 / GT4 / TT), little-endian. 010 Editor template:
 * https://github.com/Nenkai/GT-File-Specifications-Documentation/blob/master/Formats/GT4/GT4_Tex1_TexSet.bt
 *
 *   0x00 "Tex1"          0x04 u32 reloc ptr (0 on disk)   0x08 u32 0          0x0C u32 set size
 *   0x10 u16 base TBP (0, remapped at runtime)             0x12 u16 size of the set in GS blocks
 *   0x14 u16 pglu texture count                            0x16 u16 GS transfer count
 *   0x18 u32 pglu textures offset                          0x1C u32 GS transfers offset
 *   0x20 u32 clut patches offset (0 = none)                0x24 u32 clut animation offset    0x28 u32 unknown (0)
 *
 * The pglu textures are GS register sets (see PgluTexture) and only POINT into GS memory. The pixels arrive
 * through the GS transfers (0x0C bytes each: u32 data offset, u16 BP, u8 BW, u8 format, u16 width, u16 height),
 * which need not match the textures at all: a simple set has one transfer for the image and one for its CLUT,
 * but sets with several textures upload everything pre-swizzled as PSMCT32 blobs (64 x n, then 32x32, 32x16, ...)
 * whatever the texture formats are. Textures are 2^TW x 2^TH in GS terms with the real size in the clamp
 * register, so CLUTs and other textures are packed into the blocks an image leaves unused - do not be surprised
 * by a CBP in the middle of what looks like another texture. Several CLUTs can share one block through CSA.
 * Hence: replay all transfers into an emulated GS memory, then read each texture back out of it.
 */
internal sealed class Tex1Reader
{
    public const uint Magic = 0x31786554; // "Tex1"
    public const int HeaderSize = 0x30;
    public const int TransferInfoSize = 0x0C;

    private readonly PgluTexture[] _textures;
    private readonly List<uint?[]> _clutPatchSets = []; // per clut patch set, per texture: its entry, if it has one
    private readonly GsMemory _gs;
    private readonly (GsFormat Format, int Offset, int Bp, int Bw, int Width, int Height)[] _transfers;
    private int[]? _wordSources; // GS word -> offset of the 4 data bytes it was uploaded from, -1 when none
    private Dictionary<int, int>? _wordBySource; // the inverse

    public int TextureCount => _textures.Length;

    /// <summary>The emulated GS memory after every transfer was replayed.</summary>
    internal GsMemory Memory => _gs;

    /// <summary>Validates the header and returns the pglu texture count.</summary>
    public static int ReadTextureCount(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException("Tex1 data is smaller than its header.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
            throw new InvalidDataException("Not a Tex1 texture set (bad magic).");
        if (data.Length < BinaryPrimitives.ReadInt32LittleEndian(data[0x0C..]))
            throw new InvalidDataException("Tex1 data is smaller than the size its header specifies.");
        return BinaryPrimitives.ReadUInt16LittleEndian(data[0x14..]);
    }

    public Tex1Reader(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _textures = new PgluTexture[ReadTextureCount(data)];

        try
        {
            var reader = new ByteReader(data, position: 0x16);
            int transferCount = reader.ReadUInt16();
            int texturesOffset = reader.ReadInt32();
            int transfersOffset = reader.ReadInt32();
            int clutPatchesOffset = reader.ReadInt32();

            reader.Position = texturesOffset;
            for (int i = 0; i < _textures.Length; i++)
                _textures[i] = PgluTexture.Read(reader.ReadSpan(PgluTexture.Size));

            var transfers = _transfers = new (GsFormat Format, int Offset, int Bp, int Bw, int Width, int Height)[transferCount];
            for (int i = 0; i < transferCount; i++)
            {
                reader.Position = transfersOffset + i * TransferInfoSize;
                int offset = reader.ReadInt32();
                int bp = reader.ReadUInt16();
                int bw = reader.ReadByte();
                var psm = (GsPsm)reader.ReadByte();
                int width = reader.ReadUInt16();
                int height = reader.ReadUInt16();

                // PSMCT24 transfers (3 bytes per pixel) never worked in the old tools either.
                GsFormat format = GsFormat.ForSwizzle(psm) ?? throw new NotSupportedException($"Tex1 transfer format {psm} is not supported.");
                transfers[i] = (format, offset, bp, bw, width, height);
            }

            if (clutPatchesOffset != 0)
                ReadClutPatchSets(reader, clutPatchesOffset);

            long words = 0;
            foreach (var t in transfers)
                words = Math.Max(words, GsMemory.WordsReached(t.Format, t.Bp, GsMemory.PagesPerRow(t.Format, t.Bw), t.Width, t.Height));
            for (int i = 0; i < _textures.Length; i++)
                words = Math.Max(words, WordsRead(i));

            _gs = new GsMemory(words);
            foreach (var t in transfers)
            {
                reader.Position = t.Offset;
                _gs.Write(t.Format, t.Bp, t.Bw, t.Width, t.Height, reader.ReadSpan(GsFormat.GetDataSize(t.Width, t.Height, t.Format.Psm)));
            }
        }
        catch (Exception e) when (e is EndOfStreamException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException("Tex1 data is truncated or corrupt.", e);
        }
    }

    // Clut patch sets swap the CLUT pointer of some textures: alternate palettes for the same pixels. Set 0 is the
    // base look (the old dumper applied it over tex0's own CBP/CSA, so we do too); the others are VARIANTS of a
    // texture. Layout: u32 set count, u32 offsets[count]; a set is u32 count + entries CSA:5 CBP:14 format:4
    // pglu texture index:9.
    private void ReadClutPatchSets(ByteReader reader, int clutPatchesOffset)
    {
        reader.Position = clutPatchesOffset;
        uint setCount = reader.ReadUInt32();
        if (setCount > 256)
            throw new InvalidDataException("Tex1 data is corrupt (implausible clut patch set count).");

        for (int set = 0; set < setCount; set++)
        {
            reader.Position = clutPatchesOffset + 4 + set * 4;
            reader.Position = reader.ReadInt32();
            uint count = reader.ReadUInt32();
            var entries = new uint?[_textures.Length];
            for (uint i = 0; i < count; i++)
            {
                uint patch = reader.ReadUInt32();
                uint textureIndex = patch >> 23;
                if (textureIndex < entries.Length)
                    entries[textureIndex] ??= patch;
            }
            _clutPatchSets.Add(entries);
        }
    }

    /// <summary>Number of palette variants the set defines (1 = every texture only has its own palette).</summary>
    public int VariantCount => Math.Max(1, _clutPatchSets.Count);

    /// <summary>Whether variant <paramref name="variant"/> gives this texture a palette different from its base one.</summary>
    public bool HasVariant(int index, int variant) =>
        variant > 0 && variant < _clutPatchSets.Count && _clutPatchSets[variant][index] is not null
        && GetClutPointer(index, variant) != GetClutPointer(index, 0);

    private (int Cbp, int Csa) GetClutPointer(int index, int variant = 0)
    {
        uint? patch = variant < _clutPatchSets.Count ? _clutPatchSets[variant][index] : null;
        if (patch is null && variant > 0 && _clutPatchSets.Count > 0)
            patch = _clutPatchSets[0][index];
        return patch is uint value
            ? ((int)(value >> 5 & 0x3FFF), (int)(value & 0x1F))
            : (_textures[index].Cbp, _textures[index].Csa);
    }

    // The layout a texture's pixels are read with; PSMCT24 occupies memory exactly like PSMCT32.
    private static GsFormat? GetTextureLayout(GsPsm psm) => psm switch
    {
        GsPsm.PSMCT32 or GsPsm.PSMCT24 => GsFormat.CT32,
        GsPsm.PSMCT16 or GsPsm.PSMCT16S => GsFormat.CT16,
        GsPsm.PSMT8 => GsFormat.T8,
        GsPsm.PSMT4 or GsPsm.PSMT4HL or GsPsm.PSMT4HH => GsFormat.T4,
        _ => null,
    };

    public (int Width, int Height) GetSize(int index)
    {
        PgluTexture texture = _textures[index];
        int fullWidth = 1 << texture.Tw;
        int fullHeight = 1 << texture.Th;

        // A REGION_CLAMP axis has the real max texel in MAXU/MAXV. Car sets record it on REPEAT axes as well (the GS
        // ignores it there, but the memory beyond it belongs to other textures: a 384-wide REPEAT body texture is
        // declared 512 wide). Other sets leave a REPEAT axis at 0, which means the full tex0 size (cropping those
        // unconditionally turned 32x64 REPEAT atlases into 1x1 images).
        return (Real(texture.Wms, texture.MaxU, fullWidth), Real(texture.Wmt, texture.MaxV, fullHeight));

        static int Real(int mode, int max, int full) => mode == PgluTexture.RegionClamp || max != 0 ? Math.Clamp(max + 1, 1, full) : full;
    }

    // GS memory Decode(index) will read, so that memory no transfer wrote still reads as zeroes instead of failing.
    private long WordsRead(int index)
    {
        PgluTexture texture = _textures[index];
        if (GetTextureLayout(texture.Psm) is not GsFormat layout)
            return 0;

        var (width, height) = GetSize(index);
        long words = GsMemory.WordsReached(layout, texture.Tbp0, GsMemory.PagesPerRow(layout, texture.Tbw), width, height);
        if (layout.UnitsPerWord > 2)
        {
            for (int variant = 0; variant < VariantCount; variant++)
            {
                var (cbp, csa) = GetClutPointer(index, variant);
                words = Math.Max(words, (long)cbp * GsFormat.BlockWords + GsFormat.PageWords + csa * 8);
            }
        }
        return words;
    }

    /// <summary>Storage mode of a texture (PSMT4 / PSMT8 are the paletted ones).</summary>
    public GsPsm GetFormat(int index) => _textures[index].Psm;

    /// <summary>Palette size of a texture: 16, 256, or 0 when it has no palette.</summary>
    public int GetPaletteSize(int index) => GetTextureLayout(_textures[index].Psm) is GsFormat layout && layout != GsFormat.CT32
        ? (layout == GsFormat.T8 ? 256 : 16)
        : 0;

    /// <summary>
    /// Where a texture's palette lives in GS memory. Textures with the same key share one palette, so recolouring
    /// it recolours all of them.
    /// </summary>
    public (int Cbp, int Csa) GetPaletteKey(int index, int variant = 0) => GetClutPointer(index, variant);

    /// <summary>
    /// PROVENANCE: for each palette entry of a texture (in the order pixel values index it), the offset inside the
    /// Tex1 data of the 4 bytes [R,G,B,A(GS)] that entry was uploaded from - or -1 when no PSMCT32 transfer
    /// supplies it. This is what turns "a byte somewhere in a swizzled GS memory dump" into "entry 5 of the door
    /// texture's palette": car sets upload all of GS memory as big PSMCT32 blobs, in block order, so a 16-colour
    /// palette sits in the file as two separate 8-word runs.
    /// </summary>
    public int[] GetPaletteSources(int index, int variant = 0)
    {
        int size = GetPaletteSize(index);
        var sources = new int[size];
        if (size == 0)
            return sources;

        var (cbp, csa) = GetClutPointer(index, variant);
        return GetPaletteSourcesAt(cbp, csa, size);
    }

    /// <summary>As <see cref="GetPaletteSources"/>, for a palette given by its GS address rather than by a texture.</summary>
    public int[] GetPaletteSourcesAt(int cbp, int csa, int size)
    {
        var sources = new int[size];
        int[] words = _wordSources ??= BuildWordSources();
        int width = size == 256 ? 16 : 8;
        for (int i = 0; i < size; i++)
        {
            int stored = size == 256 ? TiledClutIndex(i) : i; // a 256-colour CLUT is kept in CSM1's 8x2-tile order
            long address = (long)GsMemory.Address(GsFormat.CT32, cbp, 1, stored % width, stored / width) + csa * 8;
            sources[i] = address >= 0 && address < words.Length ? words[address] : -1;
        }
        return sources;
    }

    /// <summary>
    /// The 16-colour palette slot (block pointer, even CSA) that the 4 data bytes at <paramref name="dataOffset"/>
    /// are uploaded into, or null when they are not part of a PSMCT32 transfer.
    /// </summary>
    public (int Cbp, int Csa)? LocatePaletteSlot(int dataOffset)
    {
        int[] words = _wordSources ??= BuildWordSources();
        if (_wordBySource is null)
        {
            _wordBySource = [];
            for (int word = 0; word < words.Length; word++)
            {
                if (words[word] >= 0)
                    _wordBySource[words[word]] = word;
            }
        }
        return _wordBySource.TryGetValue(dataOffset & ~3, out int address)
            ? (address / GsFormat.BlockWords, address % GsFormat.BlockWords / 8 & ~1)
            : null;
    }

    // Replays the transfers like the constructor does, but records where each GS word came from. A later transfer
    // overwrites an earlier one, as in memory; words written by a non-32-bit transfer have no single source.
    private int[] BuildWordSources()
    {
        long wordCount = 0;
        foreach (var t in _transfers)
            wordCount = Math.Max(wordCount, GsMemory.WordsReached(t.Format, t.Bp, GsMemory.PagesPerRow(t.Format, t.Bw), t.Width, t.Height));
        var words = new int[Math.Min(wordCount, (long)GsFormat.MaxBlocks * GsFormat.BlockWords)];
        Array.Fill(words, -1);

        foreach (var t in _transfers)
        {
            int pagesPerRow = GsMemory.PagesPerRow(t.Format, t.Bw);
            bool wholeWords = t.Format == GsFormat.CT32;
            for (int y = 0, i = 0; y < t.Height; y++)
            {
                for (int x = 0; x < t.Width; x++, i++)
                {
                    int word = GsMemory.Address(t.Format, t.Bp, pagesPerRow, x, y) / t.Format.UnitsPerWord;
                    if ((uint)word < (uint)words.Length)
                        words[word] = wholeWords ? t.Offset + i * 4 : -1;
                }
            }
        }
        return words;
    }

    /// <summary>Decodes one texture to RGBA, cropped to its real size.</summary>
    /// <param name="variant">0 = the texture's own palette, n = the alternate palette of clut patch set n.</param>
    public RgbaImage Decode(int index, int variant = 0)
    {
        if ((uint)index >= (uint)_textures.Length)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Texture index is out of range (the set has {_textures.Length}).");

        PgluTexture texture = _textures[index];
        GsFormat layout = GetTextureLayout(texture.Psm) ?? throw new NotSupportedException($"Tex1 texture format {texture.Psm} is not supported.");
        var (width, height) = GetSize(index);

        var image = new RgbaImage(width, height);
        Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(image.Pixels.AsSpan());

        try
        {
            if (layout == GsFormat.CT16)
            {
                // RGBA5551. Bit 15 is the alpha bit, which the GS expands through TEXA; GT's own sets use it as
                // "opaque or not", the same way their 16-bit CLUTs do.
                var texels = new ushort[pixels.Length];
                _gs.ReadCT16(texture.Tbp0, texture.Tbw, width, height, texels);
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = GsToPngAlpha(FromRgba5551(texels[i]));
            }
            else if (layout == GsFormat.CT32)
            {
                // GS alpha to PNG alpha. A 24-bit texture has no alpha byte (the GS substitutes TEXA): opaque.
                bool opaque = texture.Psm == GsPsm.PSMCT24;
                _gs.ReadCT32(texture.Tbp0, texture.Tbw, width, height, pixels);
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = opaque ? pixels[i] | 0xFF000000 : GsToPngAlpha(pixels[i]);
            }
            else
            {
                uint[] palette = ReadClut(index, variant, layout == GsFormat.T8);
                var indices = new byte[width * height];
                _gs.ReadIndices(layout, texture.Tbp0, texture.Tbw, width, height, indices);
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = palette[indices[i]];
            }
        }
        catch (IndexOutOfRangeException e)
        {
            throw new InvalidDataException($"Tex1 texture {index} points outside the 4 MB of GS memory.", e);
        }

        return image;
    }

    // The CLUT as PNG-alpha RGBA indexed by pixel value: always 8x2 (PSMT4) or 16x16 (PSMT8) at CBP, buffer width 1.
    // CSA is used the GT way, as an offset of 32 bytes per step into the block: a free 256-byte block takes four
    // 16-colour CLUTs (CSA 0, 2, 4, 6).
    private uint[] ReadClut(int index, int variant, bool is256)
    {
        var (cbp, csa) = GetClutPointer(index, variant);
        int width = is256 ? 16 : 8;
        int height = is256 ? 16 : 2;
        var clut = new uint[width * height];

        GsPsm clutFormat = _textures[index].Cpsm;
        switch (clutFormat)
        {
            case GsPsm.PSMCT32:
                _gs.ReadCT32(cbp, 1, width, height, clut, csa * 8);
                break;

            case GsPsm.PSMCT16:
                // A 16-bit palette is HALF the bytes of a 32-bit one, and it is packed that way: sixteen entries
                // in the 32 bytes at CSA's slot, not spread across 64 taking every other halfword. Eight of them
                // at CSA 0..7 fill a block exactly, which is how the originals store them - reading it as an 8x2
                // area of 16-bit pixels instead makes such a texture come out blank.
                int blockStart = cbp * GsFormat.BlockWords * 2;
                for (int i = 0; i < clut.Length; i++)
                {
                    int address = GsMemory.Address(GsFormat.CT16, cbp, 1, i % width, i / width);
                    int half = blockStart + (address - blockStart) / 2 + csa * 16;
                    clut[i] = FromRgba5551(BinaryPrimitives.ReadUInt16LittleEndian(_gs.Raw.AsSpan(half * 2)));
                }
                break;

            default:
                throw new NotSupportedException($"Tex1 CLUT format {clutFormat} is not supported.");
        }

        for (int i = 0; i < clut.Length; i++)
            clut[i] = GsToPngAlpha(clut[i]);

        if (is256)
        {
            for (int i = 0; i < 256; i++)
            {
                int tiled = TiledClutIndex(i);
                if (tiled > i)
                    (clut[i], clut[tiled]) = (clut[tiled], clut[i]);
            }
        }
        return clut;
    }

    /// <summary>RGBA5551 as the GS stores it, to a 32-bit colour with GS alpha (0 or 0x80).</summary>
    public static uint FromRgba5551(ushort texel)
    {
        uint r = (uint)(texel & 0x1F), g = (uint)(texel >> 5 & 0x1F), b = (uint)(texel >> 10 & 0x1F);
        return (r << 3 | r >> 2) | (g << 3 | g >> 2) << 8 | (b << 3 | b >> 2) << 16 | ((texel & 0x8000) != 0 ? 0x80u : 0) << 24;
    }

    /// <summary>
    /// CSM1 storage order of a 256-colour CLUT: it is kept as 8x2 tiles, which swaps entries 8..15 with 16..23
    /// in every group of 32 (the mapping is its own inverse).
    /// </summary>
    public static int TiledClutIndex(int index) => (index & 0x18) switch
    {
        0x08 => index + 8,
        0x10 => index - 8,
        _ => index,
    };

    /// <summary>
    /// GS texture alpha is 0..0x80 (0x80 = opaque) and is DOUBLED into PNG range, saturating at 255. Exact inverse
    /// of <see cref="PngToGsAlpha"/> for every GS value 0..0x80, so extract -> build reproduces the alpha bytes.
    /// </summary>
    public static uint GsToPngAlpha(uint rgba)
    {
        uint alpha = rgba >> 24;
        return (rgba & 0x00FFFFFF) | (alpha >= 0x80 ? 0xFFu : alpha * 2) << 24;
    }

    /// <summary>PNG alpha to GS alpha: HALVED, rounding up, so 255 -> 0x80 (opaque), 254 -> 127, 1 -> 1, 0 -> 0.</summary>
    public static uint PngToGsAlpha(uint rgba) => (rgba & 0x00FFFFFF) | ((rgba >> 24) + 1 >> 1) << 24;
}
