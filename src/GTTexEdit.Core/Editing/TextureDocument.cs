using System.Buffers.Binary;
using GTTexEdit.Core.Cars;
using GTTexEdit.Core.Containers;
using GTTexEdit.Core.Models;

namespace GTTexEdit.Core.Editing;

/// <summary>One model of a car, one constituent of an archive, or a texture set on its own.</summary>
public sealed class TexturePart
{
    private ModelMaterials _materials;

    internal TexturePart(string name, ContentKind kind, EditableModel model, IEnumerable<TextureSetLocation> locations)
    {
        Name = name;
        Kind = kind;
        Model = model;
        _places = [.. locations.Where(l => l.TextureCount > 0 && l.Size > 0)];
        Sets = [.. _places.Select(l => new TextureSet(model, name, l, _places)
        {
            WhyNotGrow = () => WhyNotGrowPart?.Invoke(),
            Moved = Shift,
            Relocated = Relocate,
        })];
        _materials = new ModelMaterials(model.Model);
    }

    // The sets share this array, so shifting an entry of it tells every one of them at once.
    private readonly TextureSetLocation[] _places;

    /// <summary>
    /// The model was opened up at <paramref name="at"/> to make room for something: everything from there has
    /// moved down by <paramref name="delta"/> bytes, so every set of this part is told where it is now and the
    /// materials are read again from the header the model has just had fixed.
    /// </summary>
    private void Shift(int at, int delta)
    {
        for (int i = 0; i < _places.Length; i++)
        {
            if (_places[i].Offset >= at)
                _places[i] = _places[i] with { Offset = _places[i].Offset + delta };
            if (_places[i].PointerOffset >= at)
                _places[i] = _places[i] with { PointerOffset = _places[i].PointerOffset + delta };
        }
        foreach (TextureSet set in Sets)
            set.Shift(at, delta);
        _materials = new ModelMaterials(Model.Model);
    }

    /// <summary>
    /// One set went somewhere else in the model, so the list every set reads its neighbours from says where it is
    /// now. Without this the others go on believing it is where it was, and measure their own room against a set
    /// that is no longer there.
    /// </summary>
    private void Relocate(int from, int to)
    {
        for (int i = 0; i < _places.Length; i++)
        {
            if (_places[i].Offset == from)
                _places[i] = _places[i] with { Offset = to };
        }
    }

    /// <summary>
    /// Reads the structure again, after something changed the LIST of variations rather than their contents: a GT3
    /// car keeps that list in each set's own clut patch table, and counts its material arrays in the model header.
    /// </summary>
    internal void Rebuild()
    {
        foreach (TextureSet set in Sets)
            set.Reload();
        _materials = new ModelMaterials(Model.Model);
    }

    public string Name { get; }
    public ContentKind Kind { get; }
    internal EditableModel Model { get; }

    /// <summary>What the file says about whether this part may be written back longer. Set by the document.</summary>
    internal Func<string?>? WhyNotGrowPart { get; set; }

    /// <summary>The populated texture sets. A race car keeps its LODs in the later slots.</summary>
    public IReadOnlyList<TextureSet> Sets { get; }

    /// <summary>
    /// Variations of this part, however it holds them: a GT4 colour patch, or the clut patch tables of a GT3
    /// texture set. Both mean the same thing - a whole alternative look the game switches between.
    /// </summary>
    public int VariationCount => Math.Max(Model.PaintCount, Sets.Count == 0 ? 1 : Sets.Max(s => s.VariationCount));

    /// <summary>Which mechanism switches them.</summary>
    public VariationSource Variations => Model.Patch is not null ? VariationSource.ColourPatch
        : Sets.Any(s => s.Variations == VariationSource.ClutPatch) ? VariationSource.ClutPatch
        : VariationSource.None;

    public bool IsModified => Model.IsModified;

    // ------------------------------------------------------------------------------------------- materials

    /// <summary>Every value a material holds, in the order they sit in its record.</summary>
    public static IReadOnlyList<MaterialField> MaterialFields => ModelMaterials.Fields;

    /// <summary>The paint finishes the original cars use.</summary>
    public static IReadOnlyList<MaterialPreset> Finishes => MaterialPreset.All;

    public int MaterialCount => _materials.Count;

    /// <summary>
    /// How many alternative sets of material values there are. A GT4 car has one array that its colour patch
    /// varies per paint, so this is its paint count; GT3 stores a whole array per variation, next to a base one
    /// that is none of them (measured: the base array differs from every variation's in all 38 sampled cars).
    /// </summary>
    public int MaterialSetCount => MaterialCount == 0 ? 0
        : Kind == ContentKind.ModelSet2 ? Model.PaintCount
        : _materials.Arrays.Count;

    /// <summary>Which set of material values a variation uses. GT3 keeps its base array first, so they are offset by one.</summary>
    public int MaterialSetForVariation(int variation) => Kind == ContentKind.ModelSet2
        ? Math.Clamp(variation, 0, Math.Max(0, MaterialSetCount - 1))
        : Math.Clamp(variation + (_materials.Arrays.Count > 1 ? 1 : 0), 0, Math.Max(0, MaterialSetCount - 1));

    public string MaterialSetName(int set) => MaterialSetCount <= 1 ? "the materials"
        : Kind == ContentKind.ModelSet2 ? $"variation {set}"
        : set == 0 ? "the base array" : $"variation {set - 1}";

    private int MaterialOffset(int set, int material, MaterialField field)
    {
        int array = Kind == ContentKind.ModelSet2 ? 0 : Math.Clamp(set, 0, _materials.Arrays.Count - 1);
        return _materials.Arrays[array] + material * ModelMaterials.Size + field.Offset;
    }

    // GT4 varies materials through the patch, so the set IS the variation; GT3 varies them by holding another array.
    private int MaterialVariation(int set) => Kind == ContentKind.ModelSet2 ? set : 0;

    public float GetMaterialValue(int set, int material, MaterialField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (material < 0 || material >= MaterialCount)
            throw new ArgumentOutOfRangeException(nameof(material));

        Span<byte> bytes = stackalloc byte[4];
        Model.ReadBytes(MaterialVariation(set), MaterialOffset(set, material, field), bytes);
        return field.IsInteger ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadSingleLittleEndian(bytes);
    }

    /// <summary>Sets one value of one material, for one variation only. Returns false when it already had it.</summary>
    public bool SetMaterialValue(int set, int material, MaterialField field, float value)
    {
        if (GetMaterialValue(set, material, field).Equals(value))
            return false;

        Span<byte> bytes = stackalloc byte[4];
        if (field.IsInteger)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value);
        else
            BinaryPrimitives.WriteSingleLittleEndian(bytes, value);

        Model.WriteVariation(MaterialVariation(set), MaterialOffset(set, material, field), bytes);
        return true;
    }

    /// <summary>
    /// Whether the variations give this value one of their own. A GT4 car says so itself - the colour patch covers
    /// the four bytes or it does not. A GT3 car holds a whole array per variation instead, so the only way to know
    /// is to ask whether those arrays disagree about this value. The base array is left out of that: it is none of
    /// the variations and differs from all of them in every car measured, so counting it would tint everything.
    /// </summary>
    public Coverage GetMaterialCoverage(int material, MaterialField field)
    {
        if (Kind == ContentKind.ModelSet2)
            return Model.GetCoverage(MaterialOffset(0, material, field), 4);
        if (_materials.Arrays.Count < 3)   // a base array and at least two variations to disagree
            return Coverage.None;

        float first = GetMaterialValue(1, material, field);
        for (int set = 2; set < _materials.Arrays.Count; set++)
        {
            if (!GetMaterialValue(set, material, field).Equals(first))
                return Coverage.Full;
        }
        return Coverage.None;
    }

    // --------------------------------------------------------------- materials several variations have in common

    /// <summary>
    /// The OTHER variations that read the same material array as this one, so editing its materials edits theirs
    /// too. This is how the originals are built - 161 of the 290 GT3 menu cars share an array between variations,
    /// because most colours of a car want the same paint finish - and nothing about it is an error. It only has to
    /// be said out loud before an edit, and undone on request (see <see cref="GiveVariationItsOwnMaterials"/>).
    /// </summary>
    public IReadOnlyList<int> VariationsSharingMaterials(int variation)
    {
        if (_materials.VariationTable < 0 || variation < 0 || variation >= _materials.VariationCount)
            return [];
        int mine = ArrayOfVariation(variation);
        return [.. Enumerable.Range(0, _materials.VariationCount).Where(v => v != variation && ArrayOfVariation(v) == mine)];
    }

    /// <summary>Whether this variation could be given materials of its own - it has to be sharing them first.</summary>
    public bool CanOwnMaterials(int variation) => MaterialCount > 0 && VariationsSharingMaterials(variation).Count > 0;

    /// <summary>
    /// Gives one variation a material array of its own: a copy of the one it was sharing, put on the END of the
    /// model so that nothing already in it moves, with this variation's entry in the table pointed at the copy.
    /// The other variations keep reading the original, unchanged. Returns how many bytes the model grew.
    ///
    /// The model has to grow because there is nowhere to put the copy: measured over the 161 models that share an
    /// array, the room left at the end of a model is 0 to 48 bytes and an array needs 240 to 1,600.
    /// </summary>
    public int GiveVariationItsOwnMaterials(int variation)
    {
        if (!CanOwnMaterials(variation))
        {
            throw new InvalidOperationException(_materials.VariationTable < 0
                ? "This file does not keep a material array per variation, so there is nothing to separate."
                : $"Variation {variation} already has materials of its own.");
        }

        int from = ArrayOfVariation(variation), bytes = MaterialCount * ModelMaterials.Size, was = Model.Model.Length;
        int at = Model.Append(Model.Model.AsSpan(from, bytes), alignment: 16);
        Span<byte> pointer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(pointer, at);
        Model.WriteBytes(_materials.VariationTable + variation * 4, pointer);

        _materials = new ModelMaterials(Model.Model);
        return Model.Model.Length - was;
    }

    private int ArrayOfVariation(int variation) =>
        BinaryPrimitives.ReadInt32LittleEndian(Model.Model.AsSpan(_materials.VariationTable + variation * 4));

    /// <summary>Gives one material a paint finish. Returns how many of its values changed.</summary>
    public int ApplyFinish(int set, int material, MaterialPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        int changed = 0;
        foreach (MaterialField field in MaterialFields)
        {
            if (ModelMaterials.PresetValue(preset, field) is float value && SetMaterialValue(set, material, field, value))
                changed++;
        }
        return changed;
    }
}

/// <summary>
/// An open file, seen through its textures. What that file is varies - a GT4 car, a course archive of either
/// engine, a model container on its own, a bare texture set - but the textures inside are all Tex1, so everything
/// past this point is the same.
///
/// Every edit keeps the GS layout of every texture set exactly as it was, so nothing is ever resized. Only a GT4
/// car can change length at all, and only because its colour patch may grow or shrink; every other container is
/// written back with its constituents spliced in where they were.
/// </summary>
public sealed class TextureDocument
{
    private static readonly (Car4Section Section, string Name, Car4Section? Patch)[] CarLayout =
    [
        (Car4Section.MainModel, "main", Car4Section.MainColorPatch),
        (Car4Section.WheelModel, "wheel", Car4Section.WheelColorPatch),
        (Car4Section.WingModel, "wing", null),
        (Car4Section.TireModel0, "tire0", null),
        (Car4Section.TireModel1, "tire1", null),
        (Car4Section.DriverModel, "driver", null),
    ];

    /// <summary>Where a part came from in the file it was read out of, and how long it was there.</summary>
    private sealed record Placement(int Offset, int Length, EditableModel Model);

    private readonly byte[] _file;
    private readonly Car4File? _car;
    private readonly List<Placement> _placements = [];

    private TextureDocument(string path, string format, byte[] file, Car4File? car, IReadOnlyList<TexturePart> parts)
    {
        Path = path;
        Format = format;
        _file = file;
        _car = car;
        Parts = parts;
    }

    public string Path { get; }

    /// <summary>What the file turned out to be, for the window to say so.</summary>
    public string Format { get; }

    public IReadOnlyList<TexturePart> Parts { get; }

    /// <summary>True for a GT4 race car, whose main colour patch is the separate "&lt;name&gt;.pat" file.</summary>
    public bool HasExternalMainPatch => _car?.HasExternalMainPatch == true;

    /// <summary>
    /// Variations of the whole file. A GT4 car carries them in its colour patch; a GT3 car carries them in the clut
    /// patch tables of its texture sets and one material array each. Everything else has one.
    /// </summary>
    public int VariationCount => Parts.Count > 0 ? Parts.Max(p => p.VariationCount) : 1;

    public bool IsModified => Parts.Any(p => p.IsModified);

    /// <summary>How this file switches between variations, if it does.</summary>
    public VariationSource Variations => Parts.Select(p => p.Variations).FirstOrDefault(v => v != VariationSource.None);

    /// <summary>Whether the list of variations can be changed at all.</summary>
    public bool CanEditVariations => Variations != VariationSource.None && VariationCount > 0;

    /// <summary>
    /// Whether one can be ADDED. A GT4 colour patch is a list that simply grows. A GT3 car keeps a list per model
    /// instead - which palette every texture reads, and which materials it draws with - and those are lengthened by
    /// writing them again at the end of the model, so the file gets longer (see <see cref="VariationBuilder"/>).
    /// </summary>
    public bool CanAddVariation => Variations == VariationSource.ColourPatch
        || (Variations == VariationSource.ClutPatch && WhyNotAdd is null);

    /// <summary>Why a variation cannot be added to this file, or null when one can.</summary>
    public string? WhyNotAdd => Variations switch
    {
        VariationSource.ColourPatch => null,
        VariationSource.ClutPatch => Parts.Where(p => p.Variations == VariationSource.ClutPatch)
                                          .Select(VariationBuilder.WhyNot).FirstOrDefault(why => why is not null),
        _ => "nothing in this file switches between variations.",
    };

    private IEnumerable<Pat0> Patches => Parts.Select(p => p.Model.Patch).OfType<Pat0>();

    /// <summary>
    /// Appends a copy of one variation to every part that has them, and returns its index. The copy shows exactly
    /// what it was copied from until it is given palettes or materials of its own, because that is what it shares
    /// with it - which is how the original cars are built too.
    /// </summary>
    public int AddVariation(int copyOf)
    {
        if (!CanAddVariation)
        {
            throw new InvalidOperationException("This file cannot take another variation: " +
                (WhyNotAdd ?? "nothing in it switches between them."));
        }

        int index;
        if (Variations == VariationSource.ClutPatch)
        {
            index = VariationCount;
            foreach (TexturePart part in Parts.Where(p => p.Variations == VariationSource.ClutPatch))
                VariationBuilder.Add(part, copyOf);
        }
        else
        {
            index = 0;
            foreach (Pat0 patch in Patches)
                index = patch.DuplicatePaint(Math.Clamp(copyOf, 0, patch.PaintCount - 1));
        }
        Touch();
        return index;
    }

    public void RemoveVariation(int index)
    {
        if (VariationCount <= 1)
            throw new InvalidOperationException("There has to be at least one variation.");
        index = Math.Clamp(index, 0, VariationCount - 1);

        foreach (Pat0 patch in Patches)
            patch.RemovePaint(Math.Clamp(index, 0, patch.PaintCount - 1));
        foreach (TexturePart part in Parts.Where(p => p.Variations == VariationSource.ClutPatch))
            VariationTables.Remove(part, index);
        Touch();
    }

    public void MoveVariation(int from, int to)
    {
        from = Math.Clamp(from, 0, VariationCount - 1);
        to = Math.Clamp(to, 0, VariationCount - 1);

        foreach (Pat0 patch in Patches)
            patch.MovePaint(Math.Clamp(from, 0, patch.PaintCount - 1), Math.Clamp(to, 0, patch.PaintCount - 1));
        foreach (TexturePart part in Parts.Where(p => p.Variations == VariationSource.ClutPatch))
            VariationTables.Move(part, from, to);
        Touch();
    }

    // The sets cache what each variation renders as, and some of them have to be read again because the list of
    // variations itself changed; the model also has to count as edited.
    private void Touch()
    {
        foreach (TexturePart part in Parts)
        {
            part.Model.Touch();
            if (part.Variations == VariationSource.ClutPatch)
                part.Rebuild();
            else
                foreach (TextureSet set in part.Sets)
                    set.Invalidate();
        }
    }

    public static TextureDocument Open(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        return ContentScanner.Identify(file) == ContentKind.Car4 ? OpenCar(path, file) : OpenOther(path, file);
    }

    // A GT4 car is the one container whose length can change, because its colour patch can: Car4File re-packs it.
    private static TextureDocument OpenCar(string path, byte[] file)
    {
        Car4File car = Car4File.Load(path);
        var parts = new List<TexturePart>();

        foreach (var (section, name, patchSection) in CarLayout)
        {
            if (car[section] is not byte[] model || !ModelSetTextures.IsModelSet(model))
                continue;

            byte[]? patch = patchSection switch
            {
                Car4Section.MainColorPatch => car.MainColorPatch,
                Car4Section p => car[p],
                _ => null,
            };
            var part = new TexturePart(name, ContentKind.ModelSet2, new EditableModel(name, (byte[])model.Clone(), patch),
                                       new ModelSetTextures(model).Sets);
            if (part.Sets.Count > 0)
                parts.Add(part);
        }

        if (parts.Count == 0)
            throw new InvalidDataException("The car holds no texture sets.");
        return new TextureDocument(path, "GT4 car" + (car.HasExternalMainPatch ? " (race, patch beside it)" : ""), file, car, parts);
    }

    // Everything else: find what is inside, and give each thing that holds textures its own part. Nothing here can
    // change length, so saving is the original bytes with each part written back where it came from.
    private static TextureDocument OpenOther(string path, byte[] file)
    {
        IReadOnlyList<Constituent> constituents = ContentScanner.Scan(file);
        var parts = new List<TexturePart>();
        var placements = new List<Placement>();

        foreach (Constituent constituent in constituents)
        {
            byte[] bytes = file.AsSpan(constituent.Offset, constituent.Length).ToArray();
            string name = constituent.Name.Length > 0 ? constituent.Name : ContentScanner.Describe(constituent.Kind);
            var model = new EditableModel(name, bytes, null);

            IEnumerable<TextureSetLocation> locations;
            try
            {
                locations = constituent.Kind switch
                {
                    ContentKind.ModelSet2 or ContentKind.ModelSet1 => new ModelSetTextures(bytes).Sets,
                    ContentKind.TextureSet => [OwnSet(bytes)],
                    _ => [],
                };
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException)
            {
                continue;   // a magic that validated but does not parse: leave it alone rather than guess
            }

            var part = new TexturePart(name, constituent.Kind, model, locations);
            if (part.Sets.Count == 0)
                continue;
            parts.Add(part);
            placements.Add(new Placement(constituent.Offset, constituent.Length, model));
        }

        if (parts.Count == 0)
            throw new InvalidDataException("Nothing in this file holds textures: no model container and no texture set was found in it.");

        ContentKind kind = ContentScanner.Identify(file);
        string format = kind == ContentKind.Archive
            ? $"archive, {parts.Count} part(s) with textures"
            : ContentScanner.Describe(kind);

        var document = new TextureDocument(path, format, file, null, parts);
        document._placements.AddRange(placements);
        foreach (TexturePart part in parts)
            part.WhyNotGrowPart = () => document.WhyNotGrow(part);
        return document;
    }

    /// <summary>A texture set that is a file of its own: the whole thing is one set.</summary>
    private static TextureSetLocation OwnSet(byte[] data) =>
        new(0, 0, -1, 0, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x0C)),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x14)), "the set");

    /// <summary>
    /// Writes the file to <paramref name="path"/>; a GT4 race car's main patch goes beside it as
    /// "&lt;name&gt;.pat". Anything that was not edited goes back byte for byte. Returns every file written.
    /// </summary>
    public IReadOnlyList<string> Save(string path)
    {
        var written = new List<string> { path };

        if (_car is not null)
        {
            foreach (TexturePart part in Parts)
            {
                Car4Section section = CarLayout.First(l => l.Name == part.Name).Section;
                _car.ReplaceModel(section, part.Model.Model);
                Car4Section? patchSection = CarLayout.First(l => l.Name == part.Name).Patch;
                if (patchSection is Car4Section slot && part.Model.WritePatch(standalone: slot == Car4Section.MainColorPatch && _car.HasExternalMainPatch) is byte[] patch)
                    _car.ReplaceColorPatch(slot, patch);
            }

            File.WriteAllBytes(path, _car.Write());
            if (_car.ExternalMainPatch is byte[] external)
            {
                written.Add(Car4File.ExternalPatchPath(path));
                File.WriteAllBytes(written[1], external);
            }
            MarkSaved();
            return written;
        }

        File.WriteAllBytes(path, Write());
        MarkSaved();
        return written;
    }

    /// <summary>
    /// Why a part cannot be made longer, or null when it can. A part that is a whole slot of an archive, or the
    /// only thing inside a wheel or tire file, can grow because something records where it ends; one the editor
    /// found by searching inside a slot cannot, because nothing does.
    /// </summary>
    public string? WhyNotGrow(TexturePart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (_car is not null)
            return null;   // a GT4 car is laid out again from its own table every time it is written

        Placement? placement = _placements.Find(p => ReferenceEquals(p.Model, part.Model));
        if (placement is null || !ContentScanner.LooksLikeArchive(_file, out int headerSize))
            return null;

        ArchiveWriter.Region? region = ArchiveWriter.Regions(_file, headerSize)
            .Find(r => placement.Offset >= r.Offset && placement.Offset < r.Offset + r.Length);
        if (region is null || placement.Offset == region.Offset)
            return null;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(_file.AsSpan(region.Offset));
        return placement.Offset == region.Offset + WrapperHeader && (magic == WheelMagic || magic == TireMagic)
            ? null
            : $"{part.Name} was found by searching inside slot {region.Slots[0]} of the archive, and nothing in the "
              + "file records where it ends, so it cannot be made longer";
    }

    /// <summary>Everything is on disk, so nothing is unsaved - said only once the writing has actually succeeded.</summary>
    private void MarkSaved()
    {
        foreach (TexturePart part in Parts)
            part.Model.MarkSaved();
    }

    /// <summary>
    /// The whole file as it should be written back. Every part goes into the room it came out of, so a file nothing
    /// changed comes back byte for byte. A part that GREW is the one exception: the archive is laid out again with
    /// the later constituents moved along and its offset table rewritten, which is only possible because an archive
    /// is nothing but that table (see <see cref="ArchiveWriter"/>).
    /// </summary>
    private byte[] Write()
    {
        Placement[] grown = [.. _placements.Where(p => p.Model.Model.Length != p.Length)];
        if (grown.Length == 0)
        {
            var file = (byte[])_file.Clone();
            foreach (Placement placement in _placements)
                placement.Model.Model.CopyTo(file.AsSpan(placement.Offset));
            return file;
        }

        // A file that is one thing - a model or a texture set on its own - simply is that thing.
        if (_placements.Count == 1 && _placements[0].Offset == 0 && _placements[0].Length == _file.Length)
            return _placements[0].Model.Model;

        if (!ContentScanner.LooksLikeArchive(_file, out int headerSize))
        {
            throw new InvalidOperationException(
                $"{grown[0].Model.Name} grew to {grown[0].Model.Model.Length} bytes, and this file is not an archive "
                + "whose table could be rewritten to make room for it.");
        }

        byte[] output = ArchiveWriter.Write(_file, [.. _placements.Select(p => EditFor(p, headerSize))]);

        // A GT3 car gets a fixed slot on the disc, and the game reads off the end of it rather than stopping.
        if (headerSize == Gt3CarHeader)
        {
            // Nothing in the file says whether it is a menu car or a race car, and every shipped car of either
            // kind is under the race limit - so the length cannot tell them apart, and guessing would refuse
            // menu cars their own allowance. The larger limit is the one that is certainly real; where a car
            // passes the smaller one, it is said rather than assumed.
            if (output.Length > Gt3MenuLimit)
            {
                throw new InvalidOperationException(
                    $"This would make the car {output.Length / 1024.0:0.#} KB, past the {Gt3MenuLimit / 1024} KB a "
                    + "menu car may be. The game reads a car out of a slot of that size, so a longer one is read "
                    + "off the end of it.");
            }
            GrewPastRaceLimit = output.Length > Gt3RaceLimit;
        }
        return output;
    }

    /// <summary>
    /// What to write back for one part. Usually it is simply the part, where it was. The exception is a part that
    /// GREW and is not a whole slot of the archive: a GT3 car keeps its wheel and tire models inside a wrapper
    /// ("GTTW" / "GTTR"), which is a 0x20 header - magic, a reloc pointer, its own length at 0x0C, flags, and the
    /// model's offset at 0x1C, always 0x20 - followed by the model and nothing else. So the wrapper grows with the
    /// model and says its new length in its header, and the whole wrapper is what goes back into the archive.
    /// </summary>
    private ArchiveWriter.Edit EditFor(Placement placement, int headerSize)
    {
        if (placement.Model.Model.Length == placement.Length)
            return new ArchiveWriter.Edit(placement.Offset, placement.Length, placement.Model.Model);

        ArchiveWriter.Region region = ArchiveWriter.Regions(_file, headerSize)
            .Find(r => placement.Offset >= r.Offset && placement.Offset < r.Offset + r.Length)
            ?? throw new InvalidOperationException($"{placement.Model.Name} is not inside any slot of the archive.");

        if (placement.Offset == region.Offset)
            return new ArchiveWriter.Edit(placement.Offset, placement.Length, placement.Model.Model);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(_file.AsSpan(region.Offset));
        if (placement.Offset != region.Offset + WrapperHeader || (magic != WheelMagic && magic != TireMagic))
        {
            throw new InvalidOperationException(
                $"{placement.Model.Name} grew, and what it sits in is not a wheel or tire file whose length could grow with it.");
        }

        var whole = new byte[WrapperHeader + placement.Model.Model.Length];
        _file.AsSpan(region.Offset, WrapperHeader).CopyTo(whole);
        placement.Model.Model.CopyTo(whole.AsSpan(WrapperHeader));
        BinaryPrimitives.WriteInt32LittleEndian(whole.AsSpan(0x0C), whole.Length);
        return new ArchiveWriter.Edit(region.Offset, region.Length, whole);
    }

    private const uint WheelMagic = 0x57545447;   // "GTTW"
    private const uint TireMagic = 0x52545447;    // "GTTR"
    private const int WrapperHeader = 0x20;

    /// <summary>
    /// Set when the file that was just written is past the size a GT3 RACE car may be. It is still inside what a
    /// menu car may be, and the file does not say which it is, so this is a caution rather than a refusal.
    /// </summary>
    public bool GrewPastRaceLimit { get; private set; }

    /// <summary>A GT3 car archive's table is 16 slots.</summary>
    private const int Gt3CarHeader = 0x40;

    /// <summary>The largest a GT3 MENU car archive may be - the game reads it out of a slot of this size.</summary>
    private const int Gt3MenuLimit = 0xC8000;

    /// <summary>The same for a race car, which gets a smaller slot. The biggest one the game ships is 629,952 bytes.</summary>
    private const int Gt3RaceLimit = 0xA8000;
}
