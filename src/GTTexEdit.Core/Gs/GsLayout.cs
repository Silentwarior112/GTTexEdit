namespace GTTexEdit.Core.Gs;

/// <summary>
/// One pglu texture seen as what the GS makes of it: an interpretation of a run of GS blocks (rectangle, buffer
/// width, storage mode) plus a palette of its own. Two views of the same buffer read the SAME nibbles.
/// </summary>
internal sealed class GsView
{
    public required Tex1Analysis.Texture Texture { get; init; }

    /// <summary>The GS blocks the view's authored rectangle touches.</summary>
    public required int[] Blocks { get; init; }

    public int Index => Texture.Index;
    public int Width => Texture.VisibleWidth;
    public int Height => Texture.VisibleHeight;
    public GsFormat? Format => Texture.Layout;
    public Tex1Analysis.ClutSlot? Clut => Texture.Clut;
    public int PaletteSize => Texture.PaletteSize;

    /// <summary>
    /// Identity of the texels it reads. Views with the same placement read literally the same nibbles in the same
    /// order - they differ only in palette and sampling registers. A different placement on the same blocks is a
    /// window (or a re-shaped reading of them).
    /// </summary>
    public (int Tbp, GsPsm Psm, int Tbw, int Width, int Height) Placement =>
        (Texture.Registers.Tbp0, Texture.Registers.Psm, Texture.Registers.Tbw, Width, Height);

    public string Describe() =>
        $"t{Index} {Width}x{Height} {Texture.Registers.Psm} tbp {Texture.Registers.Tbp0} tbw {Texture.Registers.Tbw}"
        + (Clut is null ? "" : $" clut {Clut.Cbp}/{Clut.Csa}");
}

/// <summary>
/// A BUFFER: a maximal set of GS blocks read as pixel data, together with every view that reads part of it. This is
/// the real unit of a car texture set - 177,303 pglu textures of car_bin are only 128,746 buffers, and the whole
/// size advantage of the originals comes from views sharing one buffer (a 4-bit index image whose meaning is chosen
/// by the palette). Editing a nibble changes EVERY view of the buffer; editing a palette entry changes exactly one.
/// </summary>
internal sealed class GsBuffer
{
    public required int Index { get; init; }
    public required GsView[] Views { get; init; }

    /// <summary>Every block of the buffer, ascending.</summary>
    public required int[] Blocks { get; init; }

    public GsFormat Format => Views[0].Format!;
    public int First => Blocks[0];
    public int Last => Blocks[^1];

    /// <summary>Distinct placements: one means pure palette sharing, more means the buffer also holds windows.</summary>
    public (int Tbp, GsPsm Psm, int Tbw, int Width, int Height)[] Placements =>
        [.. Views.Select(v => v.Placement).Distinct()];

    public bool IsShared => Views.Length > 1;
    public bool HasWindows => Placements.Length > 1;

    /// <summary>Index values that occur under any view (only meaningful for the indexed modes).</summary>
    public bool[] UsedIndices()
    {
        var used = new bool[Views[0].PaletteSize == 0 ? 1 : Views[0].PaletteSize];
        foreach (GsView view in Views)
        {
            foreach (byte index in view.Texture.Indices ?? [])
            {
                if (index < used.Length)
                    used[index] = true;
            }
        }
        return used;
    }

    /// <summary>
    /// The JOINT PALETTE TABLE - the buffer's actual content: one row per index value, one column per view, holding
    /// the colour that view gives the index. A texel picks a row; every view then shows its own column of that row.
    /// </summary>
    public uint[][] JointTable()
    {
        int rows = Views[0].PaletteSize;
        var table = new uint[rows][];
        for (int row = 0; row < rows; row++)
        {
            table[row] = new uint[Views.Length];
            for (int v = 0; v < Views.Length; v++)
                table[row][v] = Views[v].Clut is { } clut && row < clut.Colors.Length ? clut.Colors[row] : 0;
        }
        return table;
    }

    /// <summary>Blocks the same views would need if every distinct placement owned its own pixels (windows detached).</summary>
    public int DetachedBlocks()
    {
        int total = 0;
        foreach (var placement in Placements)
        {
            GsFormat format = GsFormat.ForSwizzle(placement.Psm) ?? Format;
            total += format.GetLastBlockIndex(placement.Width, placement.Height) + 1;
        }
        return total;
    }
}

/// <summary>
/// What a Tex1 set really holds: buffers of pixel data, CLUT blocks, and the blocks nothing reads. The GS transfers
/// are NOT this structure - they are only a decomposition of the whole memory image into power-of-two rectangles
/// for the upload, and carry no meaning for editing.
/// </summary>
internal sealed class GsLayout
{
    public required GsBuffer[] Buffers { get; init; }
    public required Tex1Analysis.ClutSlot[] Cluts { get; init; }

    /// <summary>Blocks below the set's block count that neither a buffer nor a CLUT reads (always zero-filled).</summary>
    public required int[] FreeBlocks { get; init; }

    public required int BlockCount { get; init; }

    public int PixelBlocks => Buffers.Sum(b => b.Blocks.Length);
    public int ClutBlocks => BlockCount - PixelBlocks - FreeBlocks.Length;

    public static GsLayout From(Tex1Analysis analysis)
    {
        var views = new List<GsView>();
        foreach (Tex1Analysis.Texture texture in analysis.Textures)
        {
            int[] blocks = BlocksOf(texture.Visible);
            if (blocks.Length > 0)
                views.Add(new GsView { Texture = texture, Blocks = blocks });
        }

        // Views that share a block share an index image: union them into buffers.
        var parent = new int[views.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        int Find(int a)
        {
            while (parent[a] != a)
                a = parent[a] = parent[parent[a]];
            return a;
        }

        var owner = new Dictionary<int, int>();
        for (int i = 0; i < views.Count; i++)
        {
            foreach (int block in views[i].Blocks)
            {
                if (owner.TryGetValue(block, out int other))
                {
                    int ra = Find(i), rb = Find(other);
                    if (ra != rb)
                        parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
                }
                else
                {
                    owner[block] = i;
                }
            }
        }

        var groups = new Dictionary<int, List<GsView>>();
        for (int i = 0; i < views.Count; i++)
        {
            if (!groups.TryGetValue(Find(i), out List<GsView>? group))
                groups[Find(i)] = group = [];
            group.Add(views[i]);
        }

        var buffers = groups.Values
            .Select(g => new { Views = g, Blocks = g.SelectMany(v => v.Blocks).Distinct().Order().ToArray() })
            .OrderBy(b => b.Blocks[0])
            .Select((b, i) => new GsBuffer { Index = i, Views = [.. b.Views.OrderBy(v => v.Index)], Blocks = b.Blocks })
            .ToArray();

        var pixel = new HashSet<int>(buffers.SelectMany(b => b.Blocks));
        var clutBlocks = new HashSet<int>();
        foreach (Tex1Analysis.ClutSlot clut in analysis.Cluts)
        {
            for (int i = 0; i < (clut.Size == 256 ? 4 : 1); i++)
                clutBlocks.Add(clut.Cbp + i);
        }

        return new GsLayout
        {
            Buffers = buffers,
            Cluts = [.. analysis.Cluts.OrderBy(c => c.Cbp).ThenBy(c => c.Csa)],
            FreeBlocks = [.. Enumerable.Range(0, analysis.BlockCount).Where(b => !pixel.Contains(b) && !clutBlocks.Contains(b))],
            BlockCount = analysis.BlockCount,
        };
    }

    private static int[] BlocksOf(NibbleSet nibbles)
    {
        var (first, last) = nibbles.BlockRange();
        if (first < 0)
            return [];
        var blocks = new List<int>();
        for (int block = first; block <= last; block++)
        {
            if (nibbles.CountInBlock(block) > 0)
                blocks.Add(block);
        }
        return [.. blocks];
    }

    /// <summary>The buffer a block belongs to, or -1.</summary>
    public int BufferOfBlock(int block)
    {
        for (int i = 0; i < Buffers.Length; i++)
        {
            if (Array.BinarySearch(Buffers[i].Blocks, block) >= 0)
                return i;
        }
        return -1;
    }
}
