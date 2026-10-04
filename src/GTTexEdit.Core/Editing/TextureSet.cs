using System.Buffers.Binary;
using System.Runtime.InteropServices;
using GTTexEdit.Core.Gs;
using GTTexEdit.Core.Imaging;
using GTTexEdit.Core.Models;

namespace GTTexEdit.Core.Editing;

/// <summary>One pglu texture: a reading of a buffer's texels through a palette of its own.</summary>
public sealed class TextureView
{
    internal TextureView(GsView view, int buffer, bool isWindow, int[][] paletteSources) =>
        Adopt(view, buffer, isWindow, paletteSources);

    /// <summary>
    /// Takes on what the set says about this view now. A set reads its own structure again whenever something
    /// changes where its palettes live, and the view keeps its identity through that, so whoever is holding it -
    /// the window, a sheet being imported - is not left pointing at where a palette USED to be.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(Format), nameof(Filter), nameof(Wrap), nameof(SourcesByVariation))]
    internal void Adopt(GsView view, int buffer, bool isWindow, int[][] paletteSources)
    {
        PgluTexture r = view.Texture.Registers;
        Index = view.Index;
        Buffer = buffer;
        Width = view.Width;
        Height = view.Height;
        Format = r.Psm.ToString().Replace("PSM", "");
        PaletteSize = view.PaletteSize;
        Cbp = view.Clut?.Cbp ?? -1;
        Csa = view.Clut?.Csa ?? -1;
        Filter = r.Mmag == 0 ? "nearest" : "linear";
        Wrap = $"{WrapName(r.Wms)}/{WrapName(r.Wmt)}";
        Tfx = r.Tfx;
        IsWindow = isWindow;
        SourcesByVariation = paletteSources;
        PaletteEntryBytes = r.Cpsm is GsPsm.PSMCT16 or GsPsm.PSMCT16S ? 2 : 4;
    }

    /// <summary>
    /// MDLS offsets of each palette entry's four bytes, per variation. They are the same array in every variation
    /// of a GT4 car - its colour patch rewrites those very bytes - but a GT3 car points each variation at a
    /// DIFFERENT palette of the same texture set, so there the offsets differ.
    /// </summary>
    internal int[][] SourcesByVariation { get; private set; }

    public int[] SourcesFor(int variation) => SourcesByVariation[Math.Clamp(variation, 0, SourcesByVariation.Length - 1)];

    /// <summary>
    /// Where one palette entry's four bytes are in the model, or -1 when this view has no such entry at all.
    /// Views of ONE buffer can have palettes of different lengths - the same memory read 8-bit by one texture and
    /// 4-bit by another is 256 entries to the first and 16 to the second - so a row of the buffer's joint table
    /// can be past the end of a view's palette, and that is a view with nothing there rather than a mistake.
    /// </summary>
    public int SourceOf(int entry, int variation = 0)
    {
        int[] sources = SourcesFor(variation);
        return entry >= 0 && entry < sources.Length ? sources[entry] : -1;
    }

    /// <summary>Position in the set's pglu texture table - the number the model's shapes refer to.</summary>
    public int Index { get; private set; }

    /// <summary>Which buffer it reads.</summary>
    public int Buffer { get; private set; }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string Format { get; private set; }

    /// <summary>16, 256, or 0 for a true-colour view.</summary>
    public int PaletteSize { get; private set; }

    /// <summary>
    /// How many bytes one palette entry is: four for the usual 32-bit colours, TWO where the palette is 16-bit
    /// (RGBA5551 - five bits a channel and one bit of alpha). GT3 courses and cars use those for about one
    /// texture in twelve; GT4 cars use none.
    /// </summary>
    public int PaletteEntryBytes { get; private set; } = 4;

    public int Cbp { get; private set; }
    public int Csa { get; private set; }
    public string Filter { get; private set; }
    public string Wrap { get; private set; }
    public int Tfx { get; private set; }

    /// <summary>It reads the buffer differently from the buffer's first view: a smaller or reshaped window into it.</summary>
    public bool IsWindow { get; private set; }

    /// <summary>MDLS offset of each palette entry's four bytes as the first variation sees them, -1 when the file does not hold it as a word.</summary>
    public int[] PaletteSources => SourcesByVariation[0];

    public string Label => $"t{Index}  {Width}x{Height}  {Format}" + (IsWindow ? "  (window)" : "");

    private static string WrapName(int mode) => mode switch { 0 => "repeat", 1 => "clamp", 2 => "region", _ => "rrepeat" };
}

/// <summary>
/// A buffer: one index image in GS memory and every view that reads it. Editing a texel changes every view of the
/// buffer at once; editing a palette entry changes exactly one view.
/// </summary>
public sealed class TextureBuffer
{
    internal TextureBuffer(int index, GsBuffer buffer, IReadOnlyList<TextureView> views)
    {
        Index = index;
        Views = views;
        Format = buffer.Views[0].Texture.Registers.Psm.ToString().Replace("PSM", "");
        Width = buffer.Views[0].Width;
        Height = buffer.Views[0].Height;
        PaletteSize = buffer.Views[0].PaletteSize;
        Blocks = buffer.Blocks.Length;
        FirstBlock = buffer.First;
        LastBlock = buffer.Last;
        HasWindows = buffer.HasWindows;
        DetachedBlocks = buffer.DetachedBlocks();
        GsFormat format = buffer.Format;
        BlocksAtDoubleSize = format.GetLastBlockIndex(Width * 2, Height * 2) + 1;
        BlocksAsEightBit = format == GsFormat.T4 ? GsFormat.T8.GetLastBlockIndex(Width, Height) + 1 : -1;
        IsUniform = views.All(v => v.Format == Format && v.PaletteSize == PaletteSize);
    }

    public int Index { get; }
    public IReadOnlyList<TextureView> Views { get; }
    public string Format { get; }

    /// <summary>Size of the buffer's first view - the shape the index image is edited in.</summary>
    public int Width { get; }
    public int Height { get; }

    public int PaletteSize { get; }
    public int Blocks { get; }
    public int FirstBlock { get; }
    public int LastBlock { get; }
    public bool HasWindows { get; }

    /// <summary>What the same views would cost if every window were given pixels of its own.</summary>
    public int DetachedBlocks { get; }

    /// <summary>Blocks the buffer would need at twice the width and height - about four times as many.</summary>
    public int BlocksAtDoubleSize { get; }

    /// <summary>Blocks it would need as an 8-bit buffer (twice), or -1 when it is not 4-bit. Each view would also need four CLUT blocks of its own.</summary>
    public int BlocksAsEightBit { get; }

    /// <summary>Every view reads it in the same storage mode. The game's five T4-in-T8 windows do not.</summary>
    public bool IsUniform { get; }

    public bool IsShared => Views.Count > 1;

    public string Label => $"buffer {Index}  {Width}x{Height} {Format}  {Blocks} blocks  {Views.Count} view" + (Views.Count == 1 ? "" : "s");
}

/// <summary>An image to put into one view.</summary>
public sealed record ViewImage(TextureView View, RgbaImage Image);

/// <summary>Tier 1: put an image into a view using the colours that are already there.</summary>
public sealed record ImportOptions
{
    /// <summary>
    /// How much the other views of the buffer count. 0 picks whatever index matches this view best and shows the
    /// others whatever falls out; 1 weighs keeping them unchanged as much as matching the new picture; higher
    /// locks them harder. Ignored when nothing else reads the buffer.
    /// </summary>
    public double ProtectOthers { get; init; } = 1.0;

    /// <summary>Diffuse the remaining error into the neighbouring texels (Floyd-Steinberg, serpentine).</summary>
    public bool Dither { get; init; }
}

/// <summary>Tier 2: re-solve a whole buffer - its index image and the palette of every view that reads it.</summary>
public sealed record RequantizeOptions
{
    /// <summary>
    /// How much the views that were given no image count against the ones that were. 1 preserves them as strongly
    /// as it matches the new pictures; higher protects them more; 0 lets them go wherever the indices land.
    /// </summary>
    public double PreserveOthers { get; init; } = 1.0;

    public bool Dither { get; init; }
}

public sealed record ImportResult(int TexelsChanged, int PaletteEntriesChanged, int PatchBytesDropped, double MeanError, string Summary);

/// <summary>How a file switches between variations.</summary>
public enum VariationSource
{
    /// <summary>It does not - there is one of everything.</summary>
    None,

    /// <summary>GT4: a colour patch (Pat0) holds different bytes for the same palette words, paint by paint.</summary>
    ColourPatch,

    /// <summary>GT3: the texture set holds several palettes and a clut patch table points each variation at one.</summary>
    ClutPatch,
}

/// <summary>How much of a variation an edit is allowed to give its own bytes.</summary>
public enum VariationMode
{
    /// <summary>
    /// Only the palette. The texels stay shared with every other variation, so the patch carries 16 or 256 words
    /// and nothing else - the way the original cars hold their number badges and liveries. How well it can match
    /// depends on how the shared index image happens to divide the picture up.
    /// </summary>
    PaletteOnly,

    /// <summary>
    /// The palette and the texels. This variation gets a picture of its own, exactly, and the patch carries the
    /// index image too - for a 64x64 8-bit texture that is 4 KB per variation instead of 1 KB. Everything stays
    /// in place; only the patch grows.
    /// </summary>
    PaletteAndPixels,
}

/// <summary>
/// One Tex1 texture set, opened for editing. Palettes and texels change in place, so the GS layout, the block count
/// and every file offset stay exactly as they were - nothing here changes the size of a texture or of the set.
/// Where an edit lands on bytes the colour patch recolours, that coverage is given up (see <see cref="EditableModel"/>).
/// </summary>
public sealed class TextureSet
{
    private readonly EditableModel _model;
    private TextureSetLocation _location;

    /// <summary>Every texture set of the same model, so this one knows what lies behind it.</summary>
    private readonly IReadOnlyList<TextureSetLocation> _others;

    /// <summary>
    /// Asks the file whether the part this set is in can be written back longer at all. A set can be perfectly
    /// able to grow inside its model and still be unwritable, because nothing in the file records where the part
    /// it sits in ends - and that has to be known BEFORE the work, not when the save fails.
    /// </summary>
    internal Func<string?>? WhyNotGrow { get; set; }
    private readonly Dictionary<int, GsView> _gsViews = [];
    private readonly Dictionary<int, Tex1Reader> _readers = [];
    private Tex1Analysis _structure = null!;   // the registers and offsets, read from the base bytes
    private GsLayout _layout = null!;

    internal TextureSet(EditableModel model, string part, TextureSetLocation location, IReadOnlyList<TextureSetLocation> others)
    {
        _model = model;
        _location = location;
        _others = others;
        Part = part;
        Read();
    }

    /// <summary>
    /// Reads the set's structure: the GS layout, and where every view's palette sits in each variation. Ordinary
    /// edits never change any of it - but removing or reordering a GT3 car's variations rewrites the very clut
    /// patch table this is built from, so <see cref="Reload"/> reads it again rather than replacing the set, which
    /// would leave whatever is on screen pointing at the old one.
    /// </summary>
    private void Read()
    {
        _stale = false;
        _readers.Clear();
        _gsViews.Clear();
        _structure = new Tex1Analysis(Data(), keepIndices: false);
        _layout = GsLayout.From(_structure);

        // GT3 switches a car's colour with the set's own clut patch table; GT4 does it with the model's colour
        // patch. Either way it is the same axis to everything above here.
        Variations = _structure.ClutPatchSetCount > 1 ? VariationSource.ClutPatch
            : _model.PaintCount > 1 ? VariationSource.ColourPatch
            : VariationSource.None;
        VariationCount = Variations switch
        {
            VariationSource.ClutPatch => _structure.ClutPatchSetCount,
            VariationSource.ColourPatch => _model.PaintCount,
            _ => 1,
        };

        // Views are kept, not replaced: whoever is holding one goes on holding the same object, now saying where
        // its palette is today (see TextureView.Adopt).
        Dictionary<int, TextureView> existing = Views.ToDictionary(v => v.Index);

        var buffers = new List<TextureBuffer>();
        var views = new List<TextureView>();
        foreach (GsBuffer buffer in _layout.Buffers)
        {
            var bufferViews = new List<TextureView>();
            foreach (GsView view in buffer.Views)
            {
                var sources = new int[VariationCount][];
                for (int variation = 0; variation < VariationCount; variation++)
                {
                    Tex1Analysis.ClutSlot? clut = Variations == VariationSource.ClutPatch
                        ? _structure.ClutFor(view.Index, variation)
                        : view.Clut;
                    sources[variation] = [.. (clut?.Sources ?? []).Select(o => o < 0 ? -1 : o + _location.Offset)];
                }
                bool isWindow = view.Placement != buffer.Views[0].Placement;
                TextureView info;
                if (existing.TryGetValue(view.Index, out TextureView? kept))
                {
                    kept.Adopt(view, buffers.Count, isWindow, sources);
                    info = kept;
                }
                else
                {
                    info = new TextureView(view, buffers.Count, isWindow, sources);
                }
                _gsViews[view.Index] = view;
                bufferViews.Add(info);
                views.Add(info);
            }
            buffers.Add(new TextureBuffer(buffers.Count, buffer, bufferViews));
        }
        Buffers = buffers;
        Views = [.. views.OrderBy(v => v.Index)];
    }

    public string Part { get; }
    public int List => _location.List;
    public int Slot => _location.Slot;

    /// <summary>How the set is addressed inside its part: a GT4 list and slot, or a GT3 set and variation.</summary>
    public string Name => $"{Part}:{_location.Label}";

    public IReadOnlyList<TextureBuffer> Buffers { get; private set; } = [];

    /// <summary>Every view of the set, in pglu texture order.</summary>
    public IReadOnlyList<TextureView> Views { get; private set; } = [];

    /// <summary>
    /// How many VARIATIONS this set has. A variation is a whole alternative picture: the same texels read through
    /// another palette, whichever way the game switches between them (see <see cref="VariationSource"/>).
    /// </summary>
    public int VariationCount { get; private set; }

    /// <summary>Which mechanism switches them.</summary>
    public VariationSource Variations { get; private set; }

    public int PaintCount => _model.PaintCount;

    // GT4 rewrites the same bytes per paint; GT3 points elsewhere, so its bytes are plain model bytes.
    private int ByteVariation(int variation) => Variations == VariationSource.ColourPatch ? variation : 0;

    /// <summary>Where the set's clut patch table sits IN THE MODEL, or -1 when it has none. See <see cref="VariationTables"/>.</summary>
    internal int ClutPatchTableOffset =>
        _structure.ClutPatchesOffset == 0 ? -1 : _location.Offset + (int)_structure.ClutPatchesOffset;

    /// <summary>Where the "Tex1" magic sits in the model.</summary>
    internal int Offset => _location.Offset;

    /// <summary>The set's own bytes, as the file holds them.</summary>
    internal byte[] RawBytes() => Data();

    /// <summary>What the set says its own length is now - the u32 at Tex1+0x0C, which growing it rewrites.</summary>
    internal int DeclaredSize => Size;

    /// <summary>How many clut patch sets it holds: one per variation, or none when it does not switch palettes.</summary>
    internal int ClutPatchSetCount => _structure.ClutPatchSetCount;

    /// <summary>The storage mode a texture's palette is held in - the CPSM of its tex0 register.</summary>
    internal int ClutFormat(int texture) => (int)_structure.Textures[texture].Registers.Cpsm;

    /// <summary>
    /// How many SMALLER copies of itself a view carries for distance (MXL of its tex1 register). Each one sits at
    /// its own block pointer, so a view that has them cannot simply be given a new size: they would have to be
    /// made and placed again too.
    /// </summary>
    public int MipLevels(TextureView view) => _structure.Textures[view.Index].Registers.Mxl;

    /// <summary>
    /// Makes sure this variation reads palettes no other variation reads, so that editing them reaches nothing
    /// else. Returns how many palettes it had to copy. Only a GT3 set can need it; a GT4 car gives each paint its
    /// own bytes through the colour patch.
    /// </summary>
    public int SeparatePalettes(int variation)
    {
        if (Variations != VariationSource.ClutPatch)
            return 0;
        variation = Math.Clamp(variation, 0, VariationCount - 1);
        return VariationBuilder.MakePalettesPrivate(_model, this, variation);
    }

    /// <summary>Whether this variation reads a palette another variation reads too.</summary>
    public bool PalettesAreShared(int variation) =>
        Variations == VariationSource.ClutPatch && VariationBuilder.PalettesAreShared(this, Math.Clamp(variation, 0, VariationCount - 1));

    public int BlockCount => _layout.BlockCount;
    public int PixelBlocks => _layout.PixelBlocks;
    public int ClutBlocks => _layout.ClutBlocks;
    public int FreeBlocks => _layout.FreeBlocks.Length;

    /// <summary>
    /// How many bytes of the model this set is. It is read from the set's own header every time rather than
    /// remembered, because adding a variation writes the set a longer palette table and says so there.
    /// </summary>
    private int Size
    {
        get
        {
            int declared = _location.Offset >= 0 && _location.Offset + 0x10 <= _model.Model.Length
                ? BinaryPrimitives.ReadInt32LittleEndian(_model.Model.AsSpan(_location.Offset + 0x0C))
                : 0;
            return Math.Min(declared > 0 ? declared : _location.Size, _model.Model.Length - _location.Offset);
        }
    }

    private byte[] Data(int variation = 0) => _model.GetPaintedRange(ByteVariation(variation), _location.Offset, Size);

    private Tex1Reader Reader(int variation)
    {
        variation = Math.Clamp(variation, 0, VariationCount - 1);
        if (!_readers.TryGetValue(variation, out Tex1Reader? reader))
            _readers[variation] = reader = new Tex1Reader(Data(variation));
        return reader;
    }

    // ------------------------------------------------------------------------------------------ changing size

    /// <summary>
    /// How much room this set has to grow into: the bytes between its end and whatever comes after it in the
    /// model. A set that ends the model can have as much as the file will take; one with something behind it has
    /// only its own padding, because everything in a model is found by an offset from the model's start and
    /// nothing may move.
    /// </summary>
    internal int Room
    {
        get
        {
            int next = _model.Model.Length;
            foreach (TextureSetLocation other in _others)
            {
                if (other.Offset > _location.Offset && other.Offset < next)
                    next = other.Offset;
            }

            // What lies after the LAST texture set is not free either: a model keeps the tables its header points
            // at, the stub headers of its empty texture set slots and its relocation table back there.
            if (_model.Limit(_location.Offset) is int limit)
            {
                if (limit > _location.Offset && limit < next)
                    next = limit;
            }
            else
            {
                // A model that cannot say where its own things are - a ModelSet1 has no table of its pointers -
                // is taken at its most cautious: the only bytes behind the set that are certainly nobody's are
                // the zeros, and the first byte that is anything else ends the room.
                int at = Math.Min(next, _location.Offset + Size);
                while (at < next && _model.Model[at] == 0)
                    at++;
                next = at;
            }
            return next - _location.Offset;
        }
    }

    /// <summary>Whether this set is the last thing in its model, and so may make the model longer.</summary>
    internal bool EndsTheModel => _others.All(o => o.Offset <= _location.Offset);

    /// <summary>Why this view cannot be given a new size, or null when it can.</summary>
    public string? WhyNotResize(TextureView view, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (SetAllocator.WhyNotResize(Structure, view.Index, width, height) is string why)
            return why;

        byte[]? rebuilt = SetAllocator.Resize(Structure, view.Index, width, height);
        if (rebuilt is null)
            return "the set cannot be laid out again at that size";
        if (rebuilt.Length > Room && WhyNotGrow?.Invoke() is string cannot)
            return cannot;
        return null;
    }

    /// <summary>
    /// Puts the set's new bytes into the model. They go back where they were when they still fit; when they do
    /// not, the MODEL is opened up behind the set so that they do - everything after the cut moves down, pointers
    /// and all - and the file grows by what the set actually needed. Only a model that cannot be opened up falls
    /// back to the old way: the set MOVES to the end, its entry in the model's texture set table is pointed at
    /// the new place, and the bytes left behind are simply never read again.
    /// </summary>
    private void Place(byte[] rebuilt)
    {
        // Only ever write over the set's OWN bytes. What lies between its old end and whatever follows is not
        // the set's to blank: a model keeps its relocation table, its symbol table and the headers of its empty
        // texture sets in exactly that gap, and clearing it takes them with it.
        int room = Room;
        if (rebuilt.Length <= room)
        {
            _model.Replace(_location.Offset, Math.Max(Size, rebuilt.Length), rebuilt);
            return;
        }
        if (_location.PointerOffset < 0)
        {
            // A set that is a whole file of its own has nothing pointing at it to repoint, so it grows where it is.
            _model.Replace(_location.Offset, room, rebuilt);
            return;
        }

        // Every texture set of a shipped model starts on 0x80, so the cut is a multiple of that and the ones
        // behind this one land on 0x80 again.
        int delta = (rebuilt.Length - room + 0x7F) & ~0x7F;
        if (_model.Insert(_location.Offset + room, delta))
        {
            Moved?.Invoke(_location.Offset + room, delta);
            _model.Replace(_location.Offset, Math.Max(Size, rebuilt.Length), rebuilt);
            _model.StateLength();
            return;
        }

        // A ModelSet1 has no table of its pointers to fix, so it cannot be opened up - but where the set is the
        // last thing in the model and NOTHING follows it at all (Room has already stopped at the first byte
        // behind it that is not zero), the model can simply be made longer. The container is written out around
        // the longer model either way.
        if (EndsTheModel && _location.Offset + room >= _model.Model.Length)
        {
            _model.Replace(_location.Offset, room, rebuilt);
            _model.StateLength();
            return;
        }

        int from = _location.Offset;
        int at = _model.Append(rebuilt, alignment: 0x40);
        Span<byte> pointer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(pointer, at);
        _model.WriteBytes(_location.PointerOffset, pointer);
        _location = _location with { Offset = at };
        Relocated?.Invoke(from, at);    // the part keeps the list every set reads its neighbours from
        _model.StateLength();
    }

    /// <summary>
    /// Told to the part when the model was opened up, so that every other set and its materials learn where they
    /// are now. Set by the part that owns this set.
    /// </summary>
    internal Action<int, int>? Moved { get; init; }

    /// <summary>Told to the part when THIS set went somewhere else in the model, for the same reason.</summary>
    internal Action<int, int>? Relocated { get; init; }

    /// <summary>
    /// The model was opened up at <paramref name="at"/>: this set moves down with everything else there.
    ///
    /// The structure is read again AT ONCE rather than marked stale, because a view remembers where its palette
    /// is as an offset into the MODEL (see Read). Deferring would leave those offsets pointing at where the set
    /// used to be - which is now inside whatever grew - and the next palette edit would write there.
    /// </summary>
    internal void Shift(int at, int delta)
    {
        if (_location.Offset < at && _location.PointerOffset < at)
            return;

        if (_location.Offset >= at)
            _location = _location with { Offset = _location.Offset + delta };
        if (_location.PointerOffset >= at)
            _location = _location with { PointerOffset = _location.PointerOffset + delta };
        Read();
    }

    /// <summary>
    /// Gives one view a new size and puts <paramref name="image"/> in it. The set is laid out again: the view gets
    /// room of its own at the end of the set's memory, and whatever nothing reads any more is squeezed out.
    ///
    /// Views that were reading the same pixels are left exactly as they were - they go on reading the old ones, so
    /// a view that was sharing is not sharing any more.
    /// </summary>
    public ImportResult Resize(TextureView view, int width, int height, RgbaImage image, ImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(image);
        if (WhyNotResize(view, width, height) is string why)
            throw new InvalidOperationException($"This texture cannot be given a new size: {why}.");

        byte[] rebuilt = SetAllocator.Resize(Structure, view.Index, width, height)!;
        int was = BlockCount, sharing = Buffers[view.Buffer].Views.Count - 1, patchedBefore = _model.PatchedBytes;

        // A GT4 car's variations are a colour patch that names bytes of the MODEL by offset. Laying the set out
        // again moves every one of those bytes, so the patch has to be taken off the set first - otherwise it goes
        // on writing its values wherever those offsets now land, which is somebody else's pixels - and then put
        // back on the words the colours have moved to.
        Dictionary<(int View, int Variation), uint[]> colours = CaptureVariations();
        if (_model.Patch is not null)
            _model.Uncover(_location.Offset, Size);

        Place(rebuilt);
        Reload();
        RestoreVariations(colours);

        TextureView resized = Views.First(v => v.Index == view.Index);
        ImportResult put = Import(resized, image, options ?? new ImportOptions());
        return put with
        {
            // what the paints really lost, which is the patch before against the patch after - the range is given
            // up over the whole set and then written back at the words the colours moved to
            PatchBytesDropped = Math.Max(0, patchedBefore - _model.PatchedBytes),
            Summary = $"t{view.Index} is now {width}x{height}; the set went from {was} to {BlockCount} blocks"
                + (sharing > 0 ? $" and no longer shares its pixels with {sharing} other view(s)" : "")
                + $". {put.Summary}",
        };
    }

    /// <summary>
    /// What every variation shows of every palette in this set, before something moves those palettes. Empty for a
    /// file whose variations are not a colour patch: a GT3 set carries its own, and they are written out with it.
    /// </summary>
    private Dictionary<(int View, int Variation), uint[]> CaptureVariations()
    {
        var colours = new Dictionary<(int, int), uint[]>();
        if (Variations != VariationSource.ColourPatch)
            return colours;

        foreach (TextureView view in Views)
        {
            if (view.PaletteSize == 0)
                continue;
            for (int variation = 1; variation < VariationCount; variation++)
            {
                var entries = new uint[view.PaletteSize];
                for (int entry = 0; entry < entries.Length; entry++)
                    entries[entry] = GetPaletteColor(view, entry, variation);
                colours[(view.Index, variation)] = entries;
            }
        }
        return colours;
    }

    /// <summary>
    /// Puts those colours back, now that the palettes have moved. Writing each one covers the words it is at, so
    /// the patch ends up describing where the colours actually are. Variation 0 needs nothing - it is the model's
    /// own bytes, which the set that was just written out already carries.
    /// </summary>
    private void RestoreVariations(Dictionary<(int View, int Variation), uint[]> colours)
    {
        foreach (var ((index, variation), entries) in colours)
        {
            TextureView? view = Views.FirstOrDefault(v => v.Index == index);
            if (view is null || view.PaletteSize != entries.Length)
                continue;
            for (int entry = 0; entry < entries.Length; entry++)
            {
                if (view.SourceOf(entry, variation) >= 0)
                    SetPaletteColor(view, entry, entries[entry], variation);
            }
        }
    }

    // ---------------------------------------------------------------------------------- changing colour depth

    /// <summary>
    /// How many colours the given pictures actually need. A buffer's texels are shared, so what matters is not how
    /// many colours any one picture uses but how many distinct COMBINATIONS they ask for between them - one per
    /// row of the joint table. Compare it against the depth you mean to use.
    /// </summary>
    public int ColoursNeeded(TextureBuffer buffer, IReadOnlyList<ViewPicture> pictures)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        // 256 is the most a texture can hold, and the count comes back whether it fits or not - so asking for the
        // largest real depth is how many are needed, without building a palette that could never exist.
        return DepthChange.Solve(buffer.Width, buffer.Height, 256, pictures).Combinations;
    }

    /// <summary>Why this buffer cannot be given a new colour depth, or null when it can.</summary>
    public string? WhyNotChangeDepth(TextureBuffer buffer, int paletteSize)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (paletteSize != 16 && paletteSize != 256)
            return "a texture has either 16 colours or 256";
        if (!buffer.IsUniform)
            return "the views of this buffer read it in different ways, so they cannot all change depth together";
        if (buffer.HasWindows)
            return "part of this buffer is read as a window into it, which would have to move as well";
        if (buffer.PaletteSize == 0)
            return "this buffer is true colour, so it has no palette to deepen";
        if (buffer.PaletteSize == paletteSize)
            return $"it already has {paletteSize} colours";
        if (Structure.ClutAnimationOffset != 0)
            return "this set animates its palettes, and that table says where things are";
        return null;
    }

    /// <summary>
    /// Gives a buffer a new colour depth. Every view of it must be given a picture, and where the file has
    /// variations, a picture for each of them - nothing is invented and nothing is squeezed to fit. If the
    /// pictures between them need more colours than the depth allows, this refuses and says how many they need.
    /// </summary>
    public ImportResult ChangeDepth(TextureBuffer buffer, int paletteSize, IReadOnlyList<ViewPicture> pictures)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(pictures);
        if (WhyNotChangeDepth(buffer, paletteSize) is string why)
            throw new InvalidOperationException($"This buffer cannot be given {paletteSize} colours: {why}.");

        int[] wanted = [.. buffer.Views.Select(v => v.Index)];
        int variations = Variations == VariationSource.ClutPatch ? VariationCount : 1;
        foreach (int view in wanted)
        {
            for (int variation = 0; variation < variations; variation++)
            {
                if (!pictures.Any(p => p.View == view && p.Variation == variation))
                {
                    throw new InvalidOperationException(
                        $"t{view} has no picture for variation {variation}. Every view of this buffer needs one for "
                        + "every variation, because they all read the same texels.");
                }
            }
        }

        DepthChange.Solution solved = DepthChange.Solve(buffer.Width, buffer.Height, paletteSize, pictures);
        if (!solved.Fits)
        {
            throw new InvalidOperationException(
                $"Between them these pictures need {solved.Combinations} colours, and this buffer can hold "
                + $"{paletteSize}. They share their texels, so what counts is how many distinct combinations of "
                + "colours they ask for, not how many each one uses. Give it content that fits.");
        }

        byte[]? rebuilt = SetAllocator.ChangeDepth(Structure, wanted, paletteSize, buffer.Width, buffer.Height,
            solved.Indices!, solved.Palettes!);
        if (rebuilt is null)
            throw new InvalidOperationException("The set cannot be laid out again at that depth.");

        // Said here rather than at the save, where it would be too late: the file may not be made longer and this
        // depth needs more room than the set has.
        if (rebuilt.Length > Room && WhyNotGrow?.Invoke() is string cannot)
            throw new InvalidOperationException($"This buffer cannot be given {paletteSize} colours: {cannot}.");

        int was = BlockCount, patchedBefore = _model.PatchedBytes;
        Dictionary<(int View, int Variation), uint[]> carried = CaptureVariations();
        if (_model.Patch is not null)
            _model.Uncover(_location.Offset, Size);
        Place(rebuilt);
        Reload();
        RestoreVariations(carried);

        // A GT4 car keeps its variations in the colour patch, so they are written on afterwards, at the new words.
        if (Variations == VariationSource.ColourPatch)
        {
            foreach (ViewPicture picture in pictures.Where(p => p.Variation > 0))
            {
                TextureView? view = Views.FirstOrDefault(v => v.Index == picture.View);
                if (view is null || !solved.Palettes!.TryGetValue((picture.View, picture.Variation), out uint[]? palette))
                    continue;
                for (int entry = 0; entry < palette.Length && entry < view.PaletteSize; entry++)
                    SetPaletteColor(view, entry, palette[entry], picture.Variation);
            }
        }

        // The patch is given up over the whole set and then written back at the new palettes, so what the paints
        // really lost is the difference - not the gross figure, which is most of the set every time.
        return new ImportResult(buffer.Width * buffer.Height, solved.Combinations,
            Math.Max(0, patchedBefore - _model.PatchedBytes), 0,
            $"buffer {buffer.Index} now has {paletteSize} colours, using {solved.Combinations} of them; "
            + $"the set went from {was} to {BlockCount} blocks");
    }

    /// <summary>
    /// Throws away what was worked out from the bytes, because they have changed. That is not only the rendered
    /// variations: the structure carries a replayed image of the set's GS memory, so anything that lays the set
    /// out again has to read it afresh or it would write out the bytes as they were before the edit.
    /// </summary>
    internal void Invalidate()
    {
        _readers.Clear();
        _stale = true;
    }

    /// <summary>The structure as the bytes say it is NOW, read again if anything has changed them.</summary>
    private Tex1Analysis Structure
    {
        get
        {
            if (_stale)
                Read();
            return _structure;
        }
    }

    private bool _stale;

    /// <summary>Reads the structure again, after the LIST of variations changed. Buffers and views are replaced.</summary>
    internal void Reload() => Read();

    // ------------------------------------------------------------------------------------------------ reading

    /// <summary>A view as the game draws it, in one variation.</summary>
    public RgbaImage Render(TextureView view, int variation = 0) => Variations == VariationSource.ClutPatch
        ? Reader(0).Decode(view.Index, Math.Clamp(variation, 0, VariationCount - 1))
        : Reader(variation).Decode(view.Index);

    /// <summary>Where one view's palette entry lives in the model - for tools that want to see what is shared.</summary>
    internal int SourceOfFor(TextureView view, int entry) => view.SourceOf(entry);

    /// <summary>The model's own bytes at an offset - for tools that want to see what is really there.</summary>
    internal void ReadAt(int offset, Span<byte> into) => _model.ReadBytes(offset, into);

    /// <summary>A palette entry as PNG-style RGBA (0xAABBGGRR); GS alpha 0..0x80 is shown doubled.</summary>
    public uint GetPaletteColor(TextureView view, int entry, int variation = 0)
    {
        ArgumentNullException.ThrowIfNull(view);
        int source = view.SourceOf(entry, variation);
        if (source < 0)
            return 0;

        Span<byte> bytes = stackalloc byte[4];
        _model.ReadBytes(ByteVariation(variation), source, bytes[..view.PaletteEntryBytes]);
        uint stored = view.PaletteEntryBytes == 2
            ? Tex1Reader.FromRgba5551(BinaryPrimitives.ReadUInt16LittleEndian(bytes))
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return Tex1Reader.GsToPngAlpha(stored);
    }

    public bool IsPaletteEntryEditable(TextureView view, int entry) => view.SourceOf(entry) >= 0;

    /// <summary>
    /// The OTHER variations that read this very palette entry, so changing it changes what they show too. A GT4
    /// car has none - its colour patch gives each paint its own bytes - but a GT3 car points variations at shared
    /// palettes wherever two colours want the same one, which is most of the time.
    /// </summary>
    public IReadOnlyList<int> VariationsSharingEntry(TextureView view, int entry, int variation)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (Variations != VariationSource.ClutPatch || entry < 0 || entry >= view.PaletteSize)
            return [];

        int mine = view.SourceOf(entry, variation);
        return mine < 0 ? [] : [.. Enumerable.Range(0, VariationCount)
            .Where(v => v != variation && view.SourceOf(entry, v) == mine)];
    }

/// <summary>Whether the variations give this entry a colour of its own.</summary>
    public Coverage GetEntryCoverage(TextureView view, int entry)
    {
        if (view.SourceOf(entry) < 0)
            return Coverage.None;
        if (Variations != VariationSource.ClutPatch)
            return _model.GetCoverage(view.SourceOf(entry), view.PaletteEntryBytes);

        // GT3 gives each variation its own palette, so an entry varies when those palettes disagree about it.
        for (int variation = 1; variation < VariationCount; variation++)
        {
            if (view.SourceOf(entry, variation) != view.SourceOf(entry))
                return Coverage.Full;
        }
        return Coverage.None;
    }

    public Coverage GetPaletteCoverage(TextureView view)
    {
        int entries = 0, covered = 0;
        foreach (int source in view.PaletteSources)
        {
            if (source < 0)
                continue;
            entries++;
            if (_model.GetCoverage(source, 4) != Coverage.None)
                covered++;
        }
        return covered == 0 ? Coverage.None : covered == entries ? Coverage.Full : Coverage.Partial;
    }

    /// <summary>Palette entries of a buffer the paints still recolour - what re-solving it would give up.</summary>
    public int PaintedEntriesIn(TextureBuffer buffer)
    {
        int painted = 0;
        foreach (TextureView view in buffer.Views)
        {
            for (int entry = 0; entry < view.PaletteSize; entry++)
            {
                if (GetEntryCoverage(view, entry) != Coverage.None)
                    painted++;
            }
        }
        return painted;
    }

    /// <summary>
    /// The joint palette table of a buffer: one row per index value, one column per view, in PNG-style RGBA.
    /// A texel picks a row; each view then shows its own column of it.
    /// </summary>
    public uint[][] GetJointTable(TextureBuffer buffer, int variation = 0)
    {
        var table = new uint[buffer.PaletteSize][];
        for (int row = 0; row < table.Length; row++)
        {
            table[row] = new uint[buffer.Views.Count];
            for (int v = 0; v < buffer.Views.Count; v++)
                table[row][v] = GetPaletteColor(buffer.Views[v], row, variation);
        }
        return table;
    }

    /// <summary>Index values some texel of the buffer uses. The rest are free capacity for a new colour.</summary>
    public bool[] GetUsedRows(TextureBuffer buffer)
    {
        var used = new bool[Math.Max(1, buffer.PaletteSize)];
        foreach (TextureView view in buffer.Views)
        {
            foreach (byte index in GetIndices(view))
            {
                if (index < used.Length)
                    used[index] = true;
            }
        }
        return used;
    }

    /// <summary>The index image of a view, one byte per texel, row-major.</summary>
    public byte[] GetIndices(TextureView view, int variation = 0)
    {
        GsView gs = _gsViews[view.Index];
        GsFormat format = gs.Format ?? throw new NotSupportedException($"Texture {view.Index} has no supported storage mode.");
        byte[] data = Data(variation);
        var indices = new byte[view.Width * view.Height];

        for (int y = 0, i = 0; y < view.Height; y++)
        {
            for (int x = 0; x < view.Width; x++, i++)
            {
                if (!TryByteOf(format, TexelAddress(gs, format, x, y), out int offset, out int shift))
                    continue;   // nothing stores it: the GS reads a zero there and so do we
                byte stored = data[offset];
                indices[i] = shift < 0 ? stored : (byte)(stored >> shift & 0xF);
            }
        }
        return indices;
    }

    // Address of texel (x, y) in the format's own units: nibbles for PSMT4, bytes for PSMT8.
    private static int TexelAddress(GsView view, GsFormat format, int x, int y)
    {
        PgluTexture r = view.Texture.Registers;
        return GsMemory.Address(format, r.Tbp0, GsMemory.PagesPerRow(format, r.Tbw), x, y);
    }

    // Tex1-relative offset of the byte holding that address, and the nibble shift inside it (-1 = the whole byte).
    // Goes through the nibble-level provenance, because a set that uploads in the texture's own storage mode -
    // which the sets in course archives do - puts a 4-bit texel in a single nibble of the file.
    //
    // False when no transfer ever wrote that texel. Some textures are declared larger than the set stores: the GS
    // reads zeroes there and the model never samples it, so it is a fact about the set, not a fault.
    private bool TryByteOf(GsFormat format, int address, out int offset, out int shift)
    {
        offset = shift = 0;
        int perTexel = format.UnitsPerWord switch
        {
            8 => 1,   // PSMT4: one nibble
            4 => 2,   // PSMT8: one byte
            _ => 0,
        };
        if (perTexel == 0)
            return false;

        int[] nibbles = _structure.NibbleSources();
        long at = (long)address * perTexel;
        int source = at >= 0 && at < nibbles.Length ? nibbles[(int)at] : -1;
        if (source < 0 || (perTexel == 2 && source % 2 != 0))
            return false;

        offset = source / 2;
        shift = perTexel == 1 ? (source & 1) * 4 : -1;
        return true;
    }

    /// <summary>
    /// Texels of a view that the file has no room for, because the set stores less than the texture declares.
    /// They read as zero and cannot be written; an import leaves them out and says so.
    /// </summary>
    public int UnstoredTexels(TextureView view)
    {
        GsView gs = _gsViews[view.Index];
        if (gs.Format is not GsFormat format)
            return 0;
        int missing = 0;
        for (int y = 0; y < view.Height; y++)
        {
            for (int x = 0; x < view.Width; x++)
            {
                if (!TryByteOf(format, TexelAddress(gs, format, x, y), out _, out _))
                    missing++;
            }
        }
        return missing;
    }

    // ------------------------------------------------------------------------------------------------ editing

    /// <summary>
    /// The bytes one palette entry becomes, and how many of them. A 16-bit palette is rounded to five bits a
    /// channel and one bit of alpha, which is all it can hold; the stored alpha is kept where the shown alpha did
    /// not change, because GS alpha above 0x80 also shows as 255 and must not be flattened by an edit to the RGB.
    /// </summary>
    private int EntryBytes(TextureView view, int source, int variation, uint color, Span<byte> bytes)
    {
        int width = view.PaletteEntryBytes;
        Span<byte> had = stackalloc byte[4];
        _model.ReadBytes(ByteVariation(variation), source, had[..width]);

        if (width == 2)
        {
            ushort was = BinaryPrimitives.ReadUInt16LittleEndian(had);
            ushort now = Tex1Reader.ToRgba5551(Tex1Reader.PngToGsAlpha(color));
            if (Tex1Reader.GsToPngAlpha(Tex1Reader.FromRgba5551(was)) >> 24 == color >> 24)
                now = (ushort)(now & 0x7FFF | was & 0x8000);     // the alpha bit is the one it had
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, now);
            return 2;
        }

        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(had);
        uint value = Tex1Reader.GsToPngAlpha(stored) >> 24 == color >> 24
            ? (color & 0x00FFFFFF) | (stored & 0xFF000000)
            : Tex1Reader.PngToGsAlpha(color);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return 4;
    }

    /// <summary>
    /// Sets a palette entry from a PNG-style colour. Reaches exactly this view: a CLUT slot has one user, and
    /// palette words never overlap pixels or another palette. Returns false when nothing changed.
    /// </summary>
    public bool SetPaletteColor(TextureView view, int entry, uint color)
    {
        int source = view.SourceOf(entry);
        if (source < 0)
            throw new InvalidOperationException("This palette entry is not stored as plain bytes in the file and cannot be edited.");
        if (GetPaletteColor(view, entry) == color)
            return false;

        Span<byte> bytes = stackalloc byte[4];
        int width = EntryBytes(view, source, 0, color, bytes);
        _model.WriteBytes(source, bytes[..width]);
        Invalidate();
        return true;
    }

    /// <summary>
    /// Sets a palette entry for ONE variation. The four bytes join the patch if they are not in it yet, so the
    /// other variations keep the colour they had. Returns false when nothing changed.
    /// </summary>
    public bool SetPaletteColor(TextureView view, int entry, uint color, int variation)
    {
        ArgumentNullException.ThrowIfNull(view);
        variation = Math.Clamp(variation, 0, VariationCount - 1);
        if (view.SourceOf(entry, variation) < 0)
            throw new InvalidOperationException("This palette entry is not stored as plain bytes in the file and cannot be edited.");
        if (GetPaletteColor(view, entry, variation) == color)
            return false;

        // A GT3 variation usually reads a palette other variations read as well, and writing this word would be
        // writing theirs. Give this one a palette of its own first; it starts as a copy, so nothing else changes.
        if (VariationsSharingEntry(view, entry, variation).Count > 0)
        {
            SeparatePalettes(variation);
            view = Views.FirstOrDefault(v => v.Index == view.Index) ?? view;
        }

        int source = view.SourceOf(entry, variation);
        if (source < 0)
            throw new InvalidOperationException("This palette entry is not stored as plain bytes in the file and cannot be edited.");

        Span<byte> bytes = stackalloc byte[4];
        int width = EntryBytes(view, source, variation, color, bytes);
        _model.WriteVariation(ByteVariation(variation), source, bytes[..width]);
        Invalidate();
        return true;
    }

    /// <summary>
    /// The best palette a view can have for one variation while its texels stay as they are: each index takes the
    /// mean of the wanted colours of the texels that use it. Indices no texel uses keep the colour they had.
    /// </summary>
    private int FitPalette(TextureView view, int variation, ReadOnlySpan<uint> wanted)
    {
        int rows = view.PaletteSize;
        byte[] indices = GetIndices(view, variation);
        var sum = new double[rows * 4];
        var count = new long[rows];
        for (int i = 0; i < indices.Length && i < wanted.Length; i++)
        {
            if (indices[i] >= rows)
                continue;
            count[indices[i]]++;
            for (int c = 0; c < 4; c++)
                sum[indices[i] * 4 + c] += wanted[i] >> (c * 8) & 0xFF;
        }

        int changed = 0;
        for (int k = 0; k < rows; k++)
        {
            if (count[k] == 0 || !IsPaletteEntryEditable(view, k))
                continue;
            uint color = 0;
            for (int c = 0; c < 4; c++)
                color |= (uint)Math.Clamp((int)Math.Round(sum[k * 4 + c] / count[k]), 0, 255) << (c * 8);
            if (SetPaletteColor(view, k, color, variation))
                changed++;
        }
        return changed;
    }

    /// <summary>
    /// What <see cref="VariationMode.PaletteOnly"/> would cost in accuracy, without writing anything: how far the
    /// shared texels let this variation get to the picture. The answer decides whether the patch needs the index
    /// image too.
    /// </summary>
    public double PreviewPaletteOnlyError(TextureView view, int variation, RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        CheckSize(view, image);
        if (view.PaletteSize == 0)
            throw new NotSupportedException("This view is true colour; it has no palette a variation could switch.");

        ReadOnlySpan<uint> wanted = MemoryMarshal.Cast<byte, uint>(image.Pixels);
        int rows = view.PaletteSize;
        byte[] indices = GetIndices(view, Math.Clamp(variation, 0, VariationCount - 1));
        var sum = new double[rows * 4];
        var count = new long[rows];
        for (int i = 0; i < indices.Length && i < wanted.Length; i++)
        {
            if (indices[i] >= rows)
                continue;
            count[indices[i]]++;
            for (int c = 0; c < 4; c++)
                sum[indices[i] * 4 + c] += wanted[i] >> (c * 8) & 0xFF;
        }

        double error = 0;
        for (int i = 0; i < indices.Length && i < wanted.Length; i++)
        {
            if (indices[i] >= rows || count[indices[i]] == 0)
                continue;
            for (int c = 0; c < 4; c++)
                error += Math.Abs((wanted[i] >> (c * 8) & 0xFF) - sum[indices[i] * 4 + c] / count[indices[i]]);
        }
        return indices.Length == 0 ? 0 : error / (indices.Length * 4.0);
    }

    /// <summary>Bytes the patch would have to carry to give one variation its own index image for this view.</summary>
    public int VariationPixelBytes(TextureView view) =>
        view.PaletteSize == 256 ? view.Width * view.Height : (view.Width * view.Height + 1) / 2;

    /// <summary>
    /// Gives ONE variation its own picture for a view. A variation is a whole alternative texture - the car's
    /// colour patch switches between them - so this is how a number badge or a livery gets replaced one variation
    /// at a time. The size and the storage mode do not change; only the patch grows.
    /// </summary>
    public ImportResult ReplaceVariation(TextureView view, int variation, RgbaImage image, VariationMode mode, bool dither = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        CheckSize(view, image);
        if (view.PaletteSize == 0)
            throw new NotSupportedException("This view is true colour; it has no palette a variation could switch.");

        variation = Math.Clamp(variation, 0, VariationCount - 1);
        ReadOnlySpan<uint> target = MemoryMarshal.Cast<byte, uint>(image.Pixels);
        TextureBuffer buffer = Buffers[view.Buffer];
        int rows = view.PaletteSize, added = _model.PatchBytesAdded, pixels = 0, entries;

        if (mode == VariationMode.PaletteOnly)
        {
            entries = FitPalette(view, variation, target);
        }
        else
        {
            // what the other views of this buffer show now, so they can be put back afterwards
            List<TextureView> others = [.. buffer.Views.Where(v => v.Index != view.Index && v.PaletteSize == rows)];
            var before = others.ToDictionary(v => v.Index, v => Render(v, variation));

            var (palette, indices) = Quantizer.Quantize(image, rows, dither);
            GsView gs = _gsViews[view.Index];
            GsFormat format = gs.Format!;
            byte[] updated = Data(variation);
            for (int y = 0, i = 0; y < view.Height; y++)
            {
                for (int x = 0; x < view.Width; x++, i++)
                {
                    if (!TryByteOf(format, TexelAddress(gs, format, x, y), out int offset, out int shift))
                        continue;
                    int value = indices[i] & (rows - 1);
                    updated[offset] = shift < 0 ? (byte)value : (byte)((updated[offset] & ~(0xF << shift)) | (value << shift));
                }
            }
            pixels = WriteChangedRuns(updated, variation);

            entries = 0;
            for (int k = 0; k < rows; k++)
            {
                if (k < palette.Length && IsPaletteEntryEditable(view, k) && SetPaletteColor(view, k, palette[k], variation))
                    entries++;
            }
            foreach (TextureView other in others)
                entries += FitPalette(other, variation, MemoryMarshal.Cast<byte, uint>(before[other.Index].Pixels));
        }

        RgbaImage got = Render(view, variation);
        double error = 0;
        for (int i = 0; i < got.Pixels.Length; i++)
            error += Math.Abs(got.Pixels[i] - image.Pixels[i]);
        error /= Math.Max(1, got.Pixels.Length);

        int grew = _model.PatchBytesAdded - added;
        string what = mode == VariationMode.PaletteOnly
            ? $"variation {variation} of t{view.Index} refitted onto the shared texels: {entries} palette entries"
            : $"variation {variation} of t{view.Index} replaced outright: {entries} palette entries and {pixels} index bytes";
        return new ImportResult(pixels, entries, 0, error,
            what + (grew > 0 ? $"; the colour patch grew by {grew} bytes" : "; the colour patch already covered it"));
    }

    /// <summary>Replaces the index image of a view. Every view of the same buffer reads these texels, so they all change.</summary>
    public int SetIndices(TextureView view, ReadOnlySpan<byte> indices)
    {
        if (indices.Length != view.Width * view.Height)
            throw new ArgumentException($"Expected {view.Width * view.Height} indices for a {view.Width}x{view.Height} texture, got {indices.Length}.", nameof(indices));

        GsView gs = _gsViews[view.Index];
        GsFormat format = gs.Format ?? throw new NotSupportedException($"Texture {view.Index} has no supported storage mode.");
        int mask = view.PaletteSize - 1;

        byte[] updated = Data();
        for (int y = 0, i = 0; y < view.Height; y++)
        {
            for (int x = 0; x < view.Width; x++, i++)
            {
                if (!TryByteOf(format, TexelAddress(gs, format, x, y), out int offset, out int shift))
                    continue;
                int value = indices[i] & mask;
                updated[offset] = shift < 0 ? (byte)value : (byte)((updated[offset] & ~(0xF << shift)) | (value << shift));
            }
        }
        return WriteChangedRuns(updated);
    }

    // Writes back only the bytes that differ, so an import that changes nothing writes nothing at all.
    // variation < 0 is the variation-unaware path, which gives up any patch coverage of what it writes.
    private int WriteChangedRuns(byte[] updated, int variation = -1)
    {
        byte[] current = Data(Math.Max(0, variation));
        int changed = 0;
        for (int i = 0; i < updated.Length;)
        {
            if (updated[i] == current[i])
            {
                i++;
                continue;
            }
            int start = i;
            while (i < updated.Length && updated[i] != current[i])
                i++;
            if (variation < 0)
                _model.WriteBytes(_location.Offset + start, updated.AsSpan(start, i - start));
            else
                _model.WriteVariation(variation, _location.Offset + start, updated.AsSpan(start, i - start));
            changed += i - start;
        }
        if (changed > 0)
            Invalidate();
        return changed;
    }

    /// <summary>
    /// TIER 1. Puts an image into one view using the colours that are already there: every texel snaps onto the
    /// index whose joint colour fits best - matching this view while keeping the other views of the buffer as
    /// close as asked. No palette changes, so no paint loses a colour.
    /// </summary>
    public ImportResult Import(TextureView view, RgbaImage image, ImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        CheckSize(view, image);
        if (view.PaletteSize == 0)
            throw new NotSupportedException("This view is true colour; there is no palette to import into.");

        ReadOnlySpan<uint> target = MemoryMarshal.Cast<byte, uint>(image.Pixels);
        TextureBuffer buffer = Buffers[view.Buffer];
        int rows = view.PaletteSize;
        uint[] mine = EntryColors(view);
        var others = new List<uint[]>();
        foreach (TextureView other in buffer.Views)
        {
            if (other.Index != view.Index && other.PaletteSize == rows)
                others.Add(EntryColors(other));
        }

        // cost of moving a texel from one row to another, summed over the other views
        var disturbance = new double[rows * rows];
        if (others.Count > 0 && options.ProtectOthers > 0)
        {
            for (int from = 0; from < rows; from++)
                for (int to = 0; to < rows; to++)
                    disturbance[from * rows + to] = others.Sum(p => Quantizer.Distance(p[from], p[to])) * options.ProtectOthers;
        }

        byte[] indices = GetIndices(view);
        var chosen = new byte[indices.Length];
        var error = options.Dither ? new float[(view.Width + 2) * view.Height * 4] : null;
        int disturbed = 0;

        for (int y = 0; y < view.Height; y++)
        {
            bool reverse = options.Dither && (y & 1) != 0;
            for (int step = 0; step < view.Width; step++)
            {
                int x = reverse ? view.Width - 1 - step : step;
                int i = y * view.Width + x;
                int at = (y * (view.Width + 2) + x + 1) * 4;
                uint wanted = error is null ? target[i] : Corrected(target[i], error, at);

                // Start from the row the texel already has, so a row that merely TIES never moves it: duplicate
                // joint colours are common and a re-import of the unchanged picture has to be a no-op.
                int from = indices[i];
                int best = from;
                double bestCost = Quantizer.Distance(mine[from], wanted);
                for (int to = 0; to < rows; to++)
                {
                    double cost = Quantizer.Distance(mine[to], wanted) + disturbance[from * rows + to];
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = to;
                    }
                }
                chosen[i] = (byte)best;
                if (best != from && disturbance[from * rows + best] > 0)
                    disturbed++;

                if (error is not null)
                {
                    for (int c = 0; c < 4; c++)
                        Diffuse(error, at, view.Width, view.Height, c, 4, (wanted >> (c * 8) & 0xFF) - (float)(mine[best] >> (c * 8) & 0xFF), reverse, x, y);
                }
            }
        }

        int before = _model.PatchBytesDropped;
        SetIndices(view, chosen);
        int moved = 0;
        for (int i = 0; i < chosen.Length; i++)
        {
            if (chosen[i] != indices[i])
                moved++;
        }

        int unstored = UnstoredTexels(view);
        string summary = (others.Count == 0
            ? $"{moved} of {chosen.Length} texels re-indexed onto the existing {rows} colours"
            : $"{moved} of {chosen.Length} texels re-indexed; {disturbed} of them also change what {others.Count} other view(s) show")
            + (unstored > 0 ? $"; {unstored} texels are declared but not stored in this set and were left out" : "");
        return new ImportResult(moved, 0, _model.PatchBytesDropped - before, MeanError(target, chosen, mine), summary);
    }

    /// <summary>
    /// TIER 2. Re-solves a whole buffer: the shared index image AND the palette of every view that reads it.
    /// A texel of a shared buffer has no colour of its own - it has one colour per view - so the index values are
    /// chosen by clustering those vectors. Views given no image keep what they show now, as strongly as
    /// <see cref="RequantizeOptions.PreserveOthers"/> asks. The footprint does not change: same blocks, same
    /// offsets. Palette entries the paints recoloured lose that: their patch coverage is dropped.
    /// </summary>
    public ImportResult RequantizeBuffer(TextureBuffer buffer, IReadOnlyList<ViewImage> targets, RequantizeOptions options)
    {
        Solution solution = Solve(buffer, targets, options);
        int changedEntries = 0, before = _model.PatchBytesDropped;
        for (int v = 0; v < solution.Views.Count; v++)
        {
            for (int row = 0; row < solution.Rows && row < solution.Centres.Length; row++)
            {
                if (IsPaletteEntryEditable(solution.Views[v], row) && SetPaletteColor(solution.Views[v], row, solution.Centres[row][v]))
                    changedEntries++;
            }
        }

        byte[] updated = Data();
        GsFormat format = _gsViews[solution.Views[0].Index].Format!;
        for (int slot = 0; slot < solution.Addresses.Length; slot++)
        {
            if (!TryByteOf(format, solution.Addresses[slot], out int offset, out int shift))
                continue;
            int value = Math.Min(solution.ClassOf[slot], solution.Rows - 1);
            updated[offset] = shift < 0 ? (byte)value : (byte)((updated[offset] & ~(0xF << shift)) | (value << shift));
        }
        int bytes = WriteChangedRuns(updated);

        double error = 0;
        int counted = 0;
        foreach (ViewImage target in targets)
        {
            RgbaImage got = Render(target.View);
            for (int i = 0; i < got.Pixels.Length; i++)
                error += Math.Abs(got.Pixels[i] - target.Image.Pixels[i]);
            counted += got.Pixels.Length;
        }

        int n = solution.Views.Count;
        return new ImportResult(bytes, changedEntries, _model.PatchBytesDropped - before, counted == 0 ? 0 : error / counted,
            $"buffer {buffer.Index} re-solved for {targets.Count} of {n} view(s): {changedEntries} palette entries and {bytes} index bytes rewritten"
            + (n > targets.Count ? $"; the other {n - targets.Count} view(s) were re-fitted onto the same indices" : ""));
    }

    /// <summary>
    /// What <see cref="RequantizeBuffer"/> would produce, without writing anything: one image per view of the
    /// buffer, in its own order. Lets an editor show the real result - which matters when one palette of 16 has
    /// to serve several pictures at once.
    /// </summary>
    public IReadOnlyList<RgbaImage> PreviewRequantize(TextureBuffer buffer, IReadOnlyList<ViewImage> targets, RequantizeOptions options)
    {
        Solution solution = Solve(buffer, targets, options);
        var images = new List<RgbaImage>();
        for (int v = 0; v < solution.Views.Count; v++)
        {
            TextureView view = solution.Views[v];
            GsView gs = _gsViews[view.Index];
            GsFormat format = gs.Format!;
            var image = new RgbaImage(view.Width, view.Height);
            Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(image.Pixels.AsSpan());
            for (int y = 0, i = 0; y < view.Height; y++)
            {
                for (int x = 0; x < view.Width; x++, i++)
                {
                    int slot = solution.SlotOf[TexelAddress(gs, format, x, y)];
                    // through the GS alpha range and back, so the preview is what the bake really stores
                    pixels[i] = Tex1Reader.GsToPngAlpha(Tex1Reader.PngToGsAlpha(solution.Centres[Math.Min(solution.ClassOf[slot], solution.Rows - 1)][v]));
                }
            }
            images.Add(image);
        }
        return images;
    }

    private sealed record Solution(IReadOnlyList<TextureView> Views, int[] Addresses, Dictionary<int, int> SlotOf, int[] ClassOf, uint[][] Centres, int Rows);

    private Solution Solve(TextureBuffer buffer, IReadOnlyList<ViewImage> targets, RequantizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(options);
        if (buffer.PaletteSize == 0)
            throw new NotSupportedException("This buffer is true colour; there is no palette to re-derive.");
        if (!buffer.IsUniform)
            throw new NotSupportedException("The views of this buffer read it in different storage modes; only their palettes can be edited.");
        foreach (ViewImage target in targets)
        {
            if (target.View.Buffer != buffer.Index)
                throw new ArgumentException($"Texture t{target.View.Index} does not read buffer {buffer.Index}.", nameof(targets));
            CheckSize(target.View, target.Image);
        }

        IReadOnlyList<TextureView> views = buffer.Views;
        int n = views.Count, rows = buffer.PaletteSize;

        // What every view should show afterwards: the image it was given, or what it shows now.
        var desired = new uint[n][];
        var weights = new double[n];
        for (int v = 0; v < n; v++)
        {
            ViewImage? given = targets.FirstOrDefault(t => t.View.Index == views[v].Index);
            desired[v] = [.. MemoryMarshal.Cast<byte, uint>((given?.Image ?? Render(views[v])).Pixels)];
            weights[v] = given is not null ? 1.0 : Math.Max(0, options.PreserveOthers);
        }

        // One slot per texel of the buffer, keyed by GS address so windows and reshaped views land on the right ones.
        var slotOf = new Dictionary<int, int>();
        var addresses = new List<int>();
        var colors = new List<uint>();
        var present = new List<bool>();
        for (int v = 0; v < n; v++)
        {
            GsView gs = _gsViews[views[v].Index];
            GsFormat format = gs.Format!;
            for (int y = 0, i = 0; y < views[v].Height; y++)
            {
                for (int x = 0; x < views[v].Width; x++, i++)
                {
                    int address = TexelAddress(gs, format, x, y);
                    if (!slotOf.TryGetValue(address, out int slot))
                    {
                        slotOf[address] = slot = addresses.Count;
                        addresses.Add(address);
                        for (int k = 0; k < n; k++)
                        {
                            colors.Add(0);
                            present.Add(false);
                        }
                    }
                    colors[slot * n + v] = desired[v][i];
                    present[slot * n + v] = true;
                }
            }
        }

        var (classOf, centres) = JointQuantizer.Cluster(addresses.Count, n, [.. colors], [.. present], weights, rows);
        Reorder(classOf, centres, targets.Count == 0 ? 0 : views.ToList().FindIndex(v => v.Index == targets[0].View.Index));

        var solution = new Solution(views, [.. addresses], slotOf, classOf, centres, rows);
        if (options.Dither)
            Redither(views, desired, weights, slotOf, classOf, centres, n);
        return solution;
    }

    // Sorts the classes dark to light by the view being edited, as the original palettes mostly are.
    private static void Reorder(int[] classOf, uint[][] centres, int by)
    {
        int reference = Math.Max(0, by);
        int[] order = [.. Enumerable.Range(0, centres.Length).OrderBy(i => Luminance(centres[i][Math.Min(reference, centres[i].Length - 1)]))];
        var rank = new int[centres.Length];
        for (int i = 0; i < order.Length; i++)
            rank[order[i]] = i;

        uint[][] sorted = [.. order.Select(i => centres[i])];
        Array.Copy(sorted, centres, sorted.Length);
        for (int i = 0; i < classOf.Length; i++)
            classOf[i] = rank[classOf[i]];

        static double Luminance(uint c) => 0.299 * (c & 0xFF) + 0.587 * (c >> 8 & 0xFF) + 0.114 * (c >> 16 & 0xFF) + 0.001 * (c >> 24);
    }

    // Error diffusion over the buffer's own shape, carrying one error per view so it means the same to all of
    // them. Texels the first view does not reach keep the class the clustering gave them.
    private void Redither(IReadOnlyList<TextureView> views, uint[][] desired, double[] weights,
                          Dictionary<int, int> slotOf, int[] classOf, uint[][] centres, int n)
    {
        TextureView primary = views[0];
        GsView gs = _gsViews[primary.Index];
        GsFormat format = gs.Format!;
        int width = primary.Width, height = primary.Height;
        var error = new float[(width + 2) * height * n * 4];

        for (int y = 0; y < height; y++)
        {
            bool reverse = (y & 1) != 0;
            for (int step = 0; step < width; step++)
            {
                int x = reverse ? width - 1 - step : step;
                if (!slotOf.TryGetValue(TexelAddress(gs, format, x, y), out int slot))
                    continue;
                int at = (y * (width + 2) + x + 1) * n * 4;
                int pixel = y * width + x;

                int best = 0;
                double bestCost = double.MaxValue;
                for (int row = 0; row < centres.Length; row++)
                {
                    double cost = 0;
                    for (int v = 0; v < n; v++)
                    {
                        uint wanted = v == 0 ? Corrected(desired[v][pixel], error, at) : desired[v][pixel];
                        for (int c = 0; c < 4; c++)
                        {
                            double delta = (wanted >> (c * 8) & 0xFF) - (double)(centres[row][v] >> (c * 8) & 0xFF);
                            cost += weights[v] * delta * delta;
                        }
                    }
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = row;
                    }
                }
                classOf[slot] = best;

                for (int c = 0; c < 4; c++)
                    Diffuse(error, at, width, height, c, n * 4, (desired[0][pixel] >> (c * 8) & 0xFF) - (float)(centres[best][0] >> (c * 8) & 0xFF), reverse, x, y);
            }
        }
    }

    private static uint Corrected(uint color, float[] error, int at)
    {
        uint corrected = 0;
        for (int c = 0; c < 4; c++)
            corrected |= (uint)(int)MathF.Round(Math.Clamp((color >> (c * 8) & 0xFF) + error[at + c], 0, 255)) << (c * 8);
        return corrected;
    }

    private static void Diffuse(float[] error, int at, int width, int height, int channel, int stride, float residual, bool reverse, int x, int y)
    {
        int step = reverse ? -stride : stride, row = (width + 2) * stride;
        if (reverse ? x > 0 : x < width - 1)
            error[at + step + channel] += residual * 7 / 16f;
        if (y < height - 1)
        {
            error[at + row + channel] += residual * 5 / 16f;
            if (reverse ? x < width - 1 : x > 0)
                error[at + row - step + channel] += residual * 3 / 16f;
            if (reverse ? x > 0 : x < width - 1)
                error[at + row + step + channel] += residual * 1 / 16f;
        }
    }

    private static void CheckSize(TextureView view, RgbaImage image)
    {
        if (image.Width != view.Width || image.Height != view.Height)
            throw new ArgumentException($"The image is {image.Width}x{image.Height}; texture t{view.Index} is {view.Width}x{view.Height}. Textures keep their size here.", nameof(image));
    }

    private uint[] EntryColors(TextureView view, int variation = 0)
    {
        var colors = new uint[view.PaletteSize];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = GetPaletteColor(view, i, variation);
        return colors;
    }

    private static double MeanError(ReadOnlySpan<uint> target, ReadOnlySpan<byte> indices, uint[] palette)
    {
        double total = 0;
        for (int i = 0; i < indices.Length; i++)
        {
            uint got = palette[indices[i]];
            for (int c = 0; c < 4; c++)
                total += Math.Abs((int)(target[i] >> (c * 8) & 0xFF) - (int)(got >> (c * 8) & 0xFF));
        }
        return indices.Length == 0 ? 0 : total / (indices.Length * 4.0);
    }
}
