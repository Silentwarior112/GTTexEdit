using System.Buffers.Binary;
using GTTexEdit.Core.Gs;

namespace GTTexEdit.Core.Editing;

/// <summary>
/// Moves things around inside a texture set's GS memory.
///
/// A block pointer is nothing but an offset added to every address the GS works out, so a run of blocks can be
/// moved anywhere as long as EVERYTHING inside it moves together and its pointer moves by the same amount. That is
/// what makes both of the things this is for possible: reclaiming the room a resized texture leaves behind, and
/// finding room for one that grew.
///
/// The runs that must move together are worked out rather than assumed. A texture's blocks are not always one
/// after another - a small texture can sit in the spare blocks of the page a bigger one is in, which is one of the
/// ways these files stay small - so anything whose span overlaps another's is treated as a single island and moved
/// as a unit. Only the space BETWEEN islands is reclaimed, which is exactly the space that nothing reads.
/// </summary>
internal static class SetAllocator
{
    private const int BlockBytes = GsFormat.BlockBytes;

    /// <summary>A run of blocks that has to move as one, first and last included.</summary>
    internal readonly record struct Island(int First, int Last)
    {
        public int Blocks => Last - First + 1;
    }

    /// <summary>
    /// Every run of blocks something reads, merged where they overlap and in order. What is not in one of these is
    /// read by nothing.
    /// </summary>
    public static List<Island> Islands(Tex1Analysis set, GsLayout layout)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(layout);

        var spans = new List<Island>();
        foreach (GsBuffer buffer in layout.Buffers)
            spans.Add(new Island(buffer.First, buffer.Last));
        foreach (Tex1Analysis.ClutSlot clut in set.Cluts)
            spans.Add(ClutSpan(clut.Cbp, clut.Csa, clut.Size, clut.Cpsm is GsPsm.PSMCT16 or GsPsm.PSMCT16S));
        foreach (Tex1Analysis.ClutPatch patch in set.ClutPatches)
        {
            if (patch.Texture >= set.Textures.Length)
                continue;
            int size = set.Textures[patch.Texture].PaletteSize;
            if (size > 0)
            {
                GsPsm cpsm = set.Textures[patch.Texture].Registers.Cpsm;
                spans.Add(ClutSpan(patch.Cbp, patch.Csa, size, cpsm is GsPsm.PSMCT16 or GsPsm.PSMCT16S));
            }
        }

        // A texture may be declared larger than the set stores, so its blocks can reach past everything above.
        foreach (Tex1Analysis.Texture t in set.Textures)
        {
            if (t.FirstBlock >= 0 && t.FullLastBlock >= t.FirstBlock)
                spans.Add(new Island(t.FirstBlock, t.FullLastBlock));
        }

        spans.Sort((a, b) => a.First.CompareTo(b.First));
        var islands = new List<Island>();
        foreach (Island span in spans)
        {
            if (islands.Count > 0 && span.First <= islands[^1].Last)
                islands[^1] = new Island(islands[^1].First, Math.Max(islands[^1].Last, span.Last));
            else
                islands.Add(span);
        }
        return islands;
    }

    /// <summary>
    /// Which blocks a palette occupies. CSA counts 32-byte slots: a 16-colour palette of 32-bit entries takes two
    /// of them (64 bytes, hence only even CSAs) and one of 16-bit entries takes a single slot, so eight of those
    /// fit a block. A 256-colour palette is four whole blocks of 32-bit entries, or two of 16-bit ones. None of
    /// them crosses out of the block CSA starts in, which is why a palette is one block unless it is a large one.
    /// </summary>
    private static Island ClutSpan(int cbp, int csa, int size, bool sixteenBit)
    {
        int bytes = size * (sixteenBit ? 2 : 4);
        int first = csa * 32, last = first + bytes - 1;
        return new Island(cbp + first / GsFormat.BlockBytes, cbp + last / GsFormat.BlockBytes);
    }

    /// <summary>
    /// The set written out again with everything nothing reads squeezed out of it. What the game sees does not
    /// change - every texture still finds its pixels and its palette - but the set can end up using fewer blocks
    /// and fewer bytes. Returns null when something in the set reads memory that is not in it, which is the one
    /// case where moving anything would be a guess.
    /// </summary>
    public static byte[]? Repack(Tex1Analysis set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var layout = GsLayout.From(set);
        List<Island> islands = Islands(set, layout);
        if (islands.Count == 0 || islands[^1].Last >= set.MemoryBlocks)
            return null;

        // where each island goes: one after another from the start
        var moved = new List<(Island Island, int To)>();
        int at = 0;
        foreach (Island island in islands)
        {
            moved.Add((island, at));
            at += island.Blocks;
        }
        if (at == set.BlockCount && islands.Count == 1 && islands[0].First == 0)
            return null;   // already packed; nothing to gain

        var memory = new GsMemory((long)at * GsFormat.BlockWords);
        foreach (var (island, to) in moved)
        {
            Buffer.BlockCopy(set.Memory.Raw, island.First * BlockBytes,
                             memory.Raw, to * BlockBytes, island.Blocks * BlockBytes);
        }

        int Delta(int block)
        {
            foreach (var (island, to) in moved)
            {
                if (block >= island.First && block <= island.Last)
                    return to - island.First;
            }
            return int.MinValue;
        }

        var textures = new PgluTexture[set.Textures.Length];
        for (int i = 0; i < textures.Length; i++)
        {
            PgluTexture registers = set.Textures[i].Registers;
            int pixels = Delta(registers.Tbp0);
            if (pixels == int.MinValue)
                return null;
            registers.Tex0 = SetBits(registers.Tex0, 0, 0x3FFF, registers.Tbp0 + pixels);

            if (set.Textures[i].PaletteSize > 0)
            {
                int palette = Delta(registers.Cbp);
                if (palette == int.MinValue)
                    return null;
                registers.Tex0 = SetBits(registers.Tex0, 37, 0x3FFF, registers.Cbp + palette);
            }

            // A texture that carries smaller copies of itself keeps a pointer to each one, and those move too.
            for (int level = 1; level <= registers.Mxl; level++)
            {
                var (tbp, _) = registers.GetMip(level);
                int mip = Delta(tbp);
                if (mip == int.MinValue)
                    return null;
                int shift = (level - 1) % 3 * 20;
                if (level <= 3)
                    registers.MipTbp1 = SetBits(registers.MipTbp1, shift, 0x3FFF, tbp + mip);
                else
                    registers.MipTbp2 = SetBits(registers.MipTbp2, shift, 0x3FFF, tbp + mip);
            }
            textures[i] = registers;
        }

        List<byte[]> bodies = SetRebuilder.ClutPatchSets(set);
        foreach (byte[] body in bodies)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(body);
            for (int i = 0; i < count; i++)
            {
                uint word = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4 + i * 4));
                int cbp = (int)(word >> 5 & 0x3FFF), delta = Delta(cbp);
                if (delta == int.MinValue)
                    return null;
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4 + i * 4),
                    (uint)SetBits(word, 5, 0x3FFF, cbp + delta));
            }
        }

        return Tex1Builder.Write(textures, memory, at, bodies, set.ClutAnimationOffset);
    }

    private static ulong SetBits(ulong value, int shift, ulong mask, int to) =>
        (value & ~(mask << shift)) | ((ulong)to & mask) << shift;

    // ------------------------------------------------------------------------------------------------ resizing

    /// <summary>The largest a texture may be here: the GS holds the real size in ten bits.</summary>
    public const int MaxSide = 1024;

    /// <summary>Why a texture cannot be given a new size, or null when it can.</summary>
    public static string? WhyNotResize(Tex1Analysis set, int texture, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (texture < 0 || texture >= set.Textures.Length)
            return "there is no such texture";
        if (width < 1 || height < 1 || width > MaxSide || height > MaxSide)
            return $"a texture has to be between 1 and {MaxSide} pixels on a side";
        if (set.Textures[texture].Layout is null)
            return "this texture is stored in a way the editor does not know how to place";
        if (set.Textures[texture].Registers.Mxl > 0)
            return "this texture carries smaller copies of itself for distance, which would have to be made again";
        if (set.ClutAnimationOffset != 0)
            return "this set animates its palettes, and that table says where things are";
        return null;
    }

    /// <summary>
    /// Gives one texture a new size. Room for it is taken at the END of the set's memory, where nothing is, and
    /// its registers are pointed at it; the pixels are left blank for the caller to fill. Anything the move leaves
    /// unread - the texture's old blocks, when nothing else was reading them - is squeezed out afterwards.
    ///
    /// Other views of the same blocks are left exactly as they were: they go on reading the old pixels, which is
    /// why those blocks may well survive. A texture that was sharing is not sharing any more.
    /// </summary>
    public static byte[]? Resize(Tex1Analysis set, int texture, int width, int height)
    {
        if (WhyNotResize(set, texture, width, height) is not null)
            return null;

        GsFormat format = set.Textures[texture].Layout!;
        int paddedWidth = Pad(width), paddedHeight = Pad(height);
        int blocks = format.GetLastBlockIndex(paddedWidth, paddedHeight) + 1;

        // Room is taken past everything anything READS, which is not always what the set says it uploads: some
        // textures are declared larger than the set stores and reach beyond its block count. Putting a resized
        // texture at that count would write it straight over one of them.
        List<Island> islands = Islands(set, GsLayout.From(set));
        int at = Math.Max(set.BlockCount, islands.Count > 0 ? islands[^1].Last + 1 : 0);
        if (at + blocks > GsFormat.MaxBlocks)
            return null;

        var memory = new GsMemory((long)(at + blocks) * GsFormat.BlockWords);
        Buffer.BlockCopy(set.Memory.Raw, 0, memory.Raw, 0, Math.Min(set.Memory.Raw.Length, at * BlockBytes));

        var textures = new PgluTexture[set.Textures.Length];
        for (int i = 0; i < textures.Length; i++)
            textures[i] = set.Textures[i].Registers;

        PgluTexture registers = textures[texture];
        registers.Tex0 = SetBits(registers.Tex0, 0, 0x3FFF, at);            // where its pixels are
        registers.Tex0 = SetBits(registers.Tex0, 14, 0x3F, BufferWidth(format, paddedWidth));
        registers.Tex0 = SetBits(registers.Tex0, 26, 0xF, Log2(paddedWidth));
        registers.Tex0 = SetBits(registers.Tex0, 30, 0xF, Log2(paddedHeight));
        registers.Clamp = SetBits(registers.Clamp, 14, 0x3FF, width - 1);              // and how much of it is real
        registers.Clamp = SetBits(registers.Clamp, 34, 0x3FF, height - 1);
        textures[texture] = registers;

        byte[] grown = Tex1Builder.Write(textures, memory, at + blocks, SetRebuilder.ClutPatchSets(set), set.ClutAnimationOffset);
        return Repack(new Tex1Analysis(grown, keepIndices: false)) ?? grown;
    }

    /// <summary>
    /// The buffer width a texture of this width needs, in the 64-pixel units the register counts. An indexed
    /// texture's page is 128 wide and the GS reads the width as pages, so its buffer width is always EVEN - a
    /// 64-pixel texture still asks for two, and simply does not use the right half.
    /// </summary>
    private static int BufferWidth(GsFormat format, int paddedWidth) => format.PageWidth == 128
        ? Math.Max(2, 2 * ((paddedWidth + 127) / 128))
        : Math.Max(1, (paddedWidth + 63) / 64);

    // ------------------------------------------------------------------------------------ changing colour depth

    /// <summary>
    /// Gives a buffer a new colour depth: room for the index image at its new size, room for the palette every
    /// view shows in every variation, and the registers and clut patch sets pointed at them. The pictures have
    /// already been worked out into an index image and palettes by <see cref="DepthChange"/> - nothing here fits
    /// or approximates anything.
    ///
    /// Palettes that are the same are placed once and shared, which is what the originals do and what keeps the
    /// cost down: a 256-colour palette is four whole blocks, so a buffer read by several views in several
    /// variations could otherwise want a great deal of room.
    /// </summary>
    public static byte[]? ChangeDepth(
        Tex1Analysis set,
        IReadOnlyList<int> textures,
        int paletteSize,
        int width,
        int height,
        byte[] indices,
        IReadOnlyDictionary<(int View, int Variation), uint[]> palettes)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(textures);
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(palettes);
        if (textures.Count == 0 || (paletteSize != 16 && paletteSize != 256))
            return null;

        GsFormat format = paletteSize == 256 ? GsFormat.T8 : GsFormat.T4;
        int paddedWidth = Pad(width), paddedHeight = Pad(height);
        int pixelBlocks = format.GetLastBlockIndex(paddedWidth, paddedHeight) + 1;

        List<Island> islands = Islands(set, GsLayout.From(set));
        int at = Math.Max(set.BlockCount, islands.Count > 0 ? islands[^1].Last + 1 : 0);
        int pixelsAt = at;
        at += pixelBlocks;

        // one place per DISTINCT palette, so views and variations that agree share it as the originals do
        var placed = new Dictionary<string, (int Cbp, int Csa)>(StringComparer.Ordinal);
        var where = new Dictionary<(int View, int Variation), (int Cbp, int Csa)>();
        int block = at, csa = 0;
        foreach (var (column, palette) in palettes.OrderBy(p => p.Key.View).ThenBy(p => p.Key.Variation))
        {
            string key = string.Join(",", palette);
            if (placed.TryGetValue(key, out var already))
            {
                where[column] = already;
                continue;
            }

            if (paletteSize == 256)
            {
                where[column] = placed[key] = (block, 0);
                block += 4;                                  // 256 entries of 32 bits is four whole blocks
            }
            else
            {
                where[column] = placed[key] = (block, csa);
                csa += 2;                                    // 16 entries of 32 bits is two 32-byte slots
                if (csa >= 8)
                {
                    csa = 0;
                    block++;
                }
            }
        }
        int total = Math.Max(at, csa > 0 ? block + 1 : block);
        if (total > GsFormat.MaxBlocks)
            return null;

        var memory = new GsMemory((long)total * GsFormat.BlockWords);
        Buffer.BlockCopy(set.Memory.Raw, 0, memory.Raw, 0, Math.Min(set.Memory.Raw.Length, pixelsAt * BlockBytes));
        memory.WriteIndices(format, pixelsAt, BufferWidth(format, paddedWidth), paddedWidth, paddedHeight,
            Spread(indices, width, height, paddedWidth, paddedHeight));
        // The pictures came in with alpha as a PNG has it, 0 to 255; the GS keeps it 0 to 128, so it is put back
        // the way it is stored - otherwise every texel of every view comes out with the wrong transparency.
        foreach (var (key, place) in placed)
        {
            uint[] palette = palettes.First(p => string.Join(",", p.Value) == key).Value;
            memory.WriteClut(place.Cbp, place.Csa, [.. palette.Select(Tex1Reader.PngToGsAlpha)]);
        }

        // the registers, and the clut patch sets that switch between the variations
        var registers = new PgluTexture[set.Textures.Length];
        for (int i = 0; i < registers.Length; i++)
            registers[i] = set.Textures[i].Registers;

        foreach (int texture in textures)
        {
            PgluTexture t = registers[texture];
            t.Tex0 = SetBits(t.Tex0, 0, 0x3FFF, pixelsAt);
            t.Tex0 = SetBits(t.Tex0, 14, 0x3F, BufferWidth(format, paddedWidth));
            t.Tex0 = SetBits(t.Tex0, 20, 0x3F, (int)(paletteSize == 256 ? GsPsm.PSMT8 : GsPsm.PSMT4));
            t.Tex0 = SetBits(t.Tex0, 26, 0xF, Log2(paddedWidth));
            t.Tex0 = SetBits(t.Tex0, 30, 0xF, Log2(paddedHeight));
            t.Tex0 = SetBits(t.Tex0, 51, 0xF, (int)GsPsm.PSMCT32);
            if (where.TryGetValue((texture, 0), out var home))
            {
                t.Tex0 = SetBits(t.Tex0, 37, 0x3FFF, home.Cbp);
                t.Tex0 = SetBits(t.Tex0, 56, 0x1F, home.Csa);
            }
            t.Clamp = SetBits(t.Clamp, 14, 0x3FF, width - 1);
            t.Clamp = SetBits(t.Clamp, 34, 0x3FF, height - 1);
            registers[texture] = t;
        }

        // A car with nothing to switch between is left with the empty patch set it came with: its registers say
        // where the palette is, so a patch would only repeat them, and that is not the shape GT3 ships - of the
        // 861 texture sets in its menu cars, the ones with a single patch set carry no patches at all. Where
        // there ARE variations, a patch already naming one of these textures still has to be brought up to date.
        List<byte[]> bodies = SetRebuilder.ClutPatchSets(set);
        bool switches = bodies.Count > 1;
        for (int variation = 0; variation < bodies.Count; variation++)
        {
            var repointed = new Dictionary<int, (int Cbp, int Csa)>();
            foreach (int texture in textures)
            {
                if (where.TryGetValue((texture, variation), out var place))
                    repointed[texture] = place;
            }
            if (repointed.Count > 0)
                bodies[variation] = RepointBody(bodies[variation], repointed, (int)GsPsm.PSMCT32, addMissing: switches);
        }

        byte[] grown = Tex1Builder.Write(registers, memory, total, bodies, set.ClutAnimationOffset);
        return Repack(new Tex1Analysis(grown, keepIndices: false)) ?? grown;
    }

    /// <summary>The index image laid into the whole addressed area, which may be larger than the real size.</summary>
    private static byte[] Spread(byte[] indices, int width, int height, int paddedWidth, int paddedHeight)
    {
        if (width == paddedWidth && height == paddedHeight)
            return indices;
        var padded = new byte[paddedWidth * paddedHeight];
        for (int y = 0; y < height; y++)
            indices.AsSpan(y * width, width).CopyTo(padded.AsSpan(y * paddedWidth));
        return padded;
    }

    /// <summary>
    /// One clut patch set with some of its textures pointed at other palettes. <paramref name="addMissing"/> says
    /// whether a texture the set does not name yet should be added to it - true only where the variations really
    /// do switch palettes, because otherwise the set is meant to stay as empty as the originals leave it.
    /// </summary>
    private static byte[] RepointBody(byte[] body, IReadOnlyDictionary<int, (int Cbp, int Csa)> repointed, int format,
                                      bool addMissing = true)
    {
        int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(body);
        var words = new List<uint>();
        var seen = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            uint word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4 + i * 4));
            int texture = (int)(word >> 23);
            if (repointed.TryGetValue(texture, out var to))
            {
                word = (uint)((to.Csa & 0x1F) | (to.Cbp & 0x3FFF) << 5 | (format & 0xF) << 19 | texture << 23);
                seen.Add(texture);
            }
            words.Add(word);
        }
        if (addMissing)
        {
            foreach (var (texture, to) in repointed.OrderBy(p => p.Key))
            {
                if (seen.Add(texture))
                    words.Add((uint)((to.Csa & 0x1F) | (to.Cbp & 0x3FFF) << 5 | (format & 0xF) << 19 | texture << 23));
            }
        }

        var written = new byte[4 + words.Count * 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(written, words.Count);
        for (int i = 0; i < words.Count; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(written.AsSpan(4 + i * 4), words[i]);
        return written;
    }

    /// <summary>The GS addresses a texture in whole powers of two; what is beyond the real size is simply not read.</summary>
    private static int Pad(int value)
    {
        int padded = 8;
        while (padded < value)
            padded <<= 1;
        return padded;
    }

    private static int Log2(int value)
    {
        int bits = 0;
        while (1 << bits < value)
            bits++;
        return bits;
    }
}
