using System.Buffers.Binary;
using GTTexEdit.Core.Models;

namespace GTTexEdit.Core.Editing;

/// <summary>
/// Adding a variation to a GT3 model.
///
/// A GT3 model says how many colours it has in two lists that the game indexes with the same colour number: the
/// CLUT PATCH SETS at the end of each texture set (which palette every texture reads) and the VARIATION MATERIALS
/// table in the model header (which material values it draws with). Measured over the 574 models of the 290 GT3
/// menu cars, those two counts are equal in 561 of them, and the exceptions are all a model that uses one mechanism
/// and not the other - never one that uses both with different lengths. Giving a car another colour therefore means
/// lengthening whichever of those lists it has, in every model that has them.
///
/// Both lists are reached through a single pointer - Tex1+0x20 for the clut patch table, the model header's 0x38
/// for the variation materials - so a longer one is written at the END of the model and the pointer moved to it.
/// Nothing that was already there moves a byte, which matters because a ModelSet1 has no relocation table and no
/// length of its own: every offset in it is measured from the model's start, and the archive's offset table is the
/// only record of where the model ends (so the file grows, and <see cref="ArchiveWriter"/> deals with that).
///
/// The new variation is a COPY: its patch set points at the palettes the copied one uses, and its materials entry
/// at the same array. That is how the originals are built - 161 of the 290 cars have two variations reading one
/// material array - and it is why the copy looks exactly like its source until it is given palettes or materials of
/// its own. The layout written here is the one PDTools' GT3 writer emits, which is known to load in-game.
/// </summary>
internal static class VariationBuilder
{
    /// <summary>
    /// Appends a copy of variation <paramref name="source"/> to every list this part carries, and returns the
    /// index of the new variation.
    /// </summary>
    public static int Add(TexturePart part, int source)
    {
        byte[] model = part.Model.Model;
        if (BinaryPrimitives.ReadUInt32LittleEndian(model) != ModelSetTextures.ModelSet1Magic)
            throw new InvalidOperationException("Only a GT3 model keeps its variations this way.");

        int added = Math.Max(part.VariationCount, 1);
        foreach (TextureSet set in part.Sets.Where(s => s.ClutPatchSetCount > 0))
            AddClutPatchSet(part, set, source);
        AddMaterialArray(part, source);
        return added;
    }

    /// <summary>
    /// Why this part cannot take another variation, or null when it can. Everything that would have to be written
    /// is checked here, so the caller never starts a change it cannot finish.
    /// </summary>
    public static string? WhyNot(TexturePart part)
    {
        byte[] model = part.Model.Model;
        if (BinaryPrimitives.ReadUInt32LittleEndian(model) != ModelSetTextures.ModelSet1Magic)
            return "this is not a GT3 model";

        // A model that keeps a whole SET OF TEXTURES per variation - what GT3 courses do, and what a race car's
        // wheels do - holds those lists after its texture sets, so the set that would have to grow is not last.
        if (BinaryPrimitives.ReadUInt16LittleEndian(model.AsSpan(0x18)) != 0
            || BinaryPrimitives.ReadInt32LittleEndian(model.AsSpan(0x34)) != 0)
            return "this model keeps a whole set of textures per variation, and those sit after the set that would have to grow";

        foreach (TextureSet set in part.Sets.Where(s => s.ClutPatchSetCount > 0))
        {
            // The clut patch table has to be the last thing in its texture set, and the set the last thing in the
            // model - that is the only reason the table can be made longer without moving anything else. Measured:
            // both hold for every one of the 574 models of the 290 GT3 menu cars.
            int end = ClutPatchesEnd(model, set);
            if (end > set.Offset + set.DeclaredSize)
                return $"the palette table of {set.Name} runs past the end of the set it is in";
            if (set.Offset + set.DeclaredSize > model.Length)
                return $"{set.Name} runs past the end of the model";
            if (part.Sets.Any(other => other.Offset > set.Offset))
                return $"{set.Name} is not the last texture set in the model, so its palette table cannot grow";
        }
        return null;
    }

    // ------------------------------------------------------------------------------------- the clut patch table

    /// <summary>
    /// Writes the texture set's clut patch table again at the end of the model, one set longer, with the new set a
    /// copy of the one being copied, and points the texture set at it.
    ///
    /// Layout, exactly as the game's own files hold it: a u32 count, then one u32 offset per set (measured from the
    /// start of the texture set), then the set bodies one after another. A body is a u32 count and that many patch
    /// words, each naming a pglu texture and the CLUT it should read.
    /// </summary>
    private static void AddClutPatchSet(TexturePart part, TextureSet set, int source)
    {
        byte[][] bodies = Bodies(part.Model.Model, set);
        WriteClutRegion(part.Model, set, [.. bodies, bodies[Math.Clamp(source, 0, bodies.Length - 1)]]);
    }

    /// <summary>One clut patch set as it sits in the file: its count and its patch words.</summary>
    private static byte[] Body(byte[] model, int setOffset, int table, int index)
    {
        int at = setOffset + BinaryPrimitives.ReadInt32LittleEndian(model.AsSpan(table + 4 + index * 4));
        int patches = BinaryPrimitives.ReadInt32LittleEndian(model.AsSpan(at));
        return model.AsSpan(at, 4 + patches * 4).ToArray();
    }

    /// <summary>Every clut patch set of a texture set, read out as it stands.</summary>
    private static byte[][] Bodies(byte[] model, TextureSet set)
    {
        int table = set.ClutPatchTableOffset, count = set.ClutPatchSetCount;
        var bodies = new byte[count][];
        for (int i = 0; i < count; i++)
            bodies[i] = Body(model, set.Offset, table, i);
        return bodies;
    }

    /// <summary>
    /// Writes a texture set's clut patch region again at the end of the model and points the set at it: a u32
    /// count, one u32 offset per set (measured from the start of the texture set), then the bodies one after
    /// another, padded so that the set's length stays align(end, 0x10).
    /// </summary>
    private static void WriteClutRegion(EditableModel editable, TextureSet set, IReadOnlyList<byte[]> bodies)
    {
        int header = 4 + bodies.Count * 4;
        var region = new byte[Align(header + bodies.Sum(b => b.Length), 16)];
        BinaryPrimitives.WriteInt32LittleEndian(region, bodies.Count);

        // The offsets are known only once the region's place in the model is: ask for the room first, then fill
        // them in against where it actually landed and write the finished region over it.
        int at = editable.Append(region, alignment: 16);
        int position = header;
        for (int i = 0; i < bodies.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(region.AsSpan(4 + i * 4), at - set.Offset + position);
            bodies[i].CopyTo(region.AsSpan(position));
            position += bodies[i].Length;
        }
        editable.WriteBytes(at, region);

        byte[] model = editable.Model;
        Write(model, set.Offset + 0x20, at - set.Offset);                  // where the table now is
        Write(model, set.Offset + 0x0C, at + region.Length - set.Offset);  // how long the set now is, exactly
    }

    /// <summary>Where the clut patch data of a texture set ends - the far end of its table and every body.</summary>
    private static int ClutPatchesEnd(byte[] model, TextureSet set)
    {
        int table = set.ClutPatchTableOffset, count = set.ClutPatchSetCount;
        int end = table + 4 + count * 4;
        for (int i = 0; i < count; i++)
        {
            int at = set.Offset + BinaryPrimitives.ReadInt32LittleEndian(model.AsSpan(table + 4 + i * 4));
            end = Math.Max(end, at + 4 + BinaryPrimitives.ReadInt32LittleEndian(model.AsSpan(at)) * 4);
        }
        return end;
    }

    // ------------------------------------------------------------------------------ palettes of a variation's own

    /// <summary>
    /// Whether this variation reads a palette that another variation reads too, so that editing it would change
    /// what that other one shows. Most of the time it does: a colour that wants the same palette as another
    /// colour simply points at it, and a variation copied from one shares every palette it had.
    /// </summary>
    public static bool PalettesAreShared(TextureSet set, int variation) =>
        set.Views.Any(v => v.PaletteSize > 0 && Shared(set, v, variation));

    private static bool Shared(TextureSet set, TextureView view, int variation) =>
        Entry(view, variation) >= 0 && set.VariationsSharingEntry(view, Slot(view, variation), variation).Count > 0;

    // The first palette entry the file actually stores as a word - the one that stands for the whole palette.
    private static int Slot(TextureView view, int variation) =>
        Array.FindIndex(view.SourcesFor(variation), o => o >= 0);

    private static int Entry(TextureView view, int variation)
    {
        int slot = Slot(view, variation);
        return slot < 0 ? -1 : view.SourcesFor(variation)[slot];
    }

    /// <summary>
    /// Gives one variation palettes of its OWN, so that editing them stops reaching the variations it was sharing
    /// them with. Returns how many palettes were copied.
    ///
    /// The new palettes need room in the set's GS memory, and a set's memory is exactly what its transfers upload -
    /// so a page of PSMCT32 is appended, along with a transfer that uploads it and a transfer table one entry
    /// longer. All of it goes on the end of the model, the set's header is pointed at the new table, and its block
    /// count and length are brought up to match; nothing that was already there moves. The palettes are then
    /// copied into the new room and this variation's clut patch set pointed at them, so it still looks exactly as
    /// it did - until something edits it, which is now its own business.
    /// </summary>
    public static int MakePalettesPrivate(EditableModel editable, TextureSet set, int variation)
    {
        ArgumentNullException.ThrowIfNull(editable);
        ArgumentNullException.ThrowIfNull(set);
        if (set.ClutPatchSetCount == 0 || variation < 0 || variation >= set.ClutPatchSetCount)
            return 0;

        // Views that read the same palette as each other keep doing so: it is only the other VARIATIONS we are
        // separating from, and a palette two views of this variation share is shared on purpose.
        var groups = new Dictionary<(int Source, int Size), List<TextureView>>();
        foreach (TextureView view in set.Views)
        {
            if (view.PaletteSize == 0 || !Shared(set, view, variation))
                continue;
            var key = (Entry(view, variation), view.PaletteSize);
            if (!groups.TryGetValue(key, out List<TextureView>? sharing))
                groups[key] = sharing = [];
            sharing.Add(view);
        }
        if (groups.Count == 0)
            return 0;

        // What those palettes hold now, read as raw words so nothing is lost on the way through.
        var contents = new Dictionary<(int, int), byte[]>();
        foreach (var (key, views) in groups)
        {
            int[] sources = views[0].SourcesFor(variation);
            var bytes = new byte[key.Item2 * 4];
            for (int i = 0; i < key.Item2; i++)
            {
                if (sources[i] >= 0)
                    editable.Model.AsSpan(sources[i], 4).CopyTo(bytes.AsSpan(i * 4));
            }
            contents[key] = bytes;
        }

        // Room for them: a 16-colour palette is a quarter of a block (four fit, at CSA 0, 2, 4 and 6) and a
        // 256-colour palette is four whole blocks. Whole pages are appended, because that is the unit a transfer
        // of PSMCT32 with a buffer width of 1 covers.
        int smalls = groups.Count(g => g.Key.Size != 256), larges = groups.Count(g => g.Key.Size == 256);
        int blocks = (smalls + 3) / 4 + larges * 4;
        int pages = (blocks + BlocksPerPage - 1) / BlocksPerPage;
        int firstBlock = Align(BinaryPrimitives.ReadUInt16LittleEndian(editable.Model.AsSpan(set.Offset + 0x12)), BlocksPerPage);

        var placement = new Dictionary<(int, int), (int Cbp, int Csa)>();
        int block = firstBlock, csa = 0;
        foreach (var key in groups.Keys.OrderByDescending(k => k.Size))   // the whole-block ones first
        {
            if (key.Size == 256)
            {
                placement[key] = (block, 0);
                block += 4;
                continue;
            }
            placement[key] = (block, csa);
            csa += 2;
            if (csa >= 8)
            {
                csa = 0;
                block++;
            }
        }

        AppendPages(editable, set, firstBlock, pages);

        // Point this variation - and only this one - at the new palettes.
        byte[][] bodies = Bodies(editable.Model, set);
        var repointed = new Dictionary<int, (int Cbp, int Csa)>();
        foreach (var (key, views) in groups)
        {
            foreach (TextureView view in views)
                repointed[view.Index] = placement[key];
        }
        bodies[variation] = Repoint(bodies[variation], set, repointed);
        WriteClutRegion(editable, set, bodies);

        // The set now reads differently, so ask it again where everything is, and fill the new palettes in.
        set.Reload();
        foreach (var (key, views) in groups)
        {
            int[] sources = set.Views.First(v => v.Index == views[0].Index).SourcesFor(variation);
            for (int i = 0; i < key.Item2; i++)
            {
                if (sources[i] >= 0)
                    editable.WriteBytes(sources[i], contents[key].AsSpan(i * 4, 4));
            }
        }
        set.Reload();
        return groups.Count;
    }

    /// <summary>32 blocks of PSMCT32 with a buffer width of 1 - the page a transfer of 64x32 covers.</summary>
    private const int BlocksPerPage = 32;

    /// <summary>
    /// Appends blank GS pages to a texture set: the data itself, a transfer that uploads each page, and the
    /// transfer table written again one entry longer with the set pointed at it.
    /// </summary>
    private static void AppendPages(EditableModel editable, TextureSet set, int firstBlock, int pages)
    {
        const int PageBytes = BlocksPerPage * 256;
        int dataAt = editable.Append(new byte[pages * PageBytes], alignment: 16);

        byte[] model = editable.Model;
        int transfers = BinaryPrimitives.ReadUInt16LittleEndian(model.AsSpan(set.Offset + 0x16));
        int transfersAt = set.Offset + (int)BinaryPrimitives.ReadUInt32LittleEndian(model.AsSpan(set.Offset + 0x1C));

        var table = new byte[(transfers + pages) * Gs.Tex1Reader.TransferInfoSize];
        model.AsSpan(transfersAt, transfers * Gs.Tex1Reader.TransferInfoSize).CopyTo(table);
        for (int page = 0; page < pages; page++)
        {
            int at = (transfers + page) * Gs.Tex1Reader.TransferInfoSize;
            BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(at), dataAt + page * PageBytes - set.Offset);
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(at + 4), (ushort)(firstBlock + page * BlocksPerPage));
            table[at + 6] = 1;                                  // buffer width
            table[at + 7] = (byte)Gs.GsPsm.PSMCT32;
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(at + 8), 64);
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(at + 10), 32);
        }

        int tableAt = editable.Append(table, alignment: 16);
        model = editable.Model;
        Write(model, set.Offset + 0x1C, tableAt - set.Offset);
        BinaryPrimitives.WriteUInt16LittleEndian(model.AsSpan(set.Offset + 0x16), (ushort)(transfers + pages));
        BinaryPrimitives.WriteUInt16LittleEndian(model.AsSpan(set.Offset + 0x12), (ushort)(firstBlock + pages * BlocksPerPage));
    }

    /// <summary>
    /// One clut patch set with some of its textures pointed at other palettes. A texture the set did not mention
    /// is added to it, because a variation that says nothing about a texture reads whatever the base says.
    /// </summary>
    private static byte[] Repoint(byte[] body, TextureSet set, IReadOnlyDictionary<int, (int Cbp, int Csa)> repointed)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(body);
        var words = new List<uint>();
        var seen = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4 + i * 4));
            int texture = (int)(word >> 23);
            if (repointed.TryGetValue(texture, out var to))
            {
                word = Word(texture, (int)(word >> 19 & 0xF), to.Cbp, to.Csa);
                seen.Add(texture);
            }
            words.Add(word);
        }
        foreach (var (texture, to) in repointed.OrderBy(p => p.Key))
        {
            if (seen.Add(texture))
                words.Add(Word(texture, set.ClutFormat(texture), to.Cbp, to.Csa));
        }

        var written = new byte[4 + words.Count * 4];
        BinaryPrimitives.WriteInt32LittleEndian(written, words.Count);
        for (int i = 0; i < words.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(written.AsSpan(4 + i * 4), words[i]);
        return written;
    }

    /// <summary>A clut patch word: the texture it is about, and the CLUT it should read.</summary>
    private static uint Word(int texture, int format, int cbp, int csa) =>
        (uint)(csa & 0x1F) | (uint)(cbp & 0x3FFF) << 5 | (uint)(format & 0xF) << 19 | (uint)texture << 23;

    // -------------------------------------------------------------------------------- the variation materials

    /// <summary>
    /// Writes the model's variation material table again at the end of the model, one entry longer, with the new
    /// entry naming the same array the copied variation reads - which is what the originals do. The variation can
    /// be given an array of its own afterwards (<see cref="TexturePart.GiveVariationItsOwnMaterials"/>).
    /// </summary>
    private static void AddMaterialArray(TexturePart part, int source)
    {
        byte[] model = part.Model.Model;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(model.AsSpan(0x1A));
        int table = BinaryPrimitives.ReadInt32LittleEndian(model.AsSpan(0x38));
        if (count == 0 || table <= 0 || table + count * 4 > model.Length)
            return;   // this model varies its textures but not its materials

        var entries = new byte[(count + 1) * 4];
        model.AsSpan(table, count * 4).CopyTo(entries);
        model.AsSpan(table + Math.Clamp(source, 0, count - 1) * 4, 4).CopyTo(entries.AsSpan(count * 4));

        int at = part.Model.Append(entries, alignment: 4);
        model = part.Model.Model;
        Write(model, 0x38, at);
        BinaryPrimitives.WriteUInt16LittleEndian(model.AsSpan(0x1A), (ushort)(count + 1));
    }

    private static void Write(byte[] model, int at, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(model.AsSpan(at), value);

    private static int Align(int value, int to) => (value + to - 1) & ~(to - 1);
}
