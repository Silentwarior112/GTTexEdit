using GTTexEdit.Core.Cars;
using GTTexEdit.Core.Models;

namespace GTTexEdit.Core.Editing;

/// <summary>How much of something the colour patch still recolours.</summary>
public enum Coverage { None, Partial, Full }

/// <summary>
/// A ModelSet2 opened for editing, with the colour patch that gives the car its VARIATIONS. A variation is not a
/// tint: the same texels read through another palette are another picture, which is how a car carries several
/// number badges or liveries in one texture. So the model has two ways to be written:
///
/// * <see cref="WriteVariation"/> - what one variation shows. The range is covered by the patch if it is not
///   already, and only that variation's bytes change. Variation 0 is also written into the model itself, because
///   that is what the originals do (paint 0's patch values are exactly the bytes stored in the model).
/// * <see cref="WriteBytes"/> - variation-unaware, for edits that cannot mean anything per variation. It GIVES UP
///   any patch coverage of the range first: a patch describes the bytes that were there, and once they change it
///   describes nothing.
///
/// Either way the rest of the patch is untouched and still written back exactly as it was read.
/// </summary>
public sealed class EditableModel
{
    private byte[] _mdls;

    public EditableModel(string name, byte[] mdls, byte[]? patch)
    {
        Name = name;
        _mdls = mdls;
        Patch = patch is not null && Pat0.IsPat0(patch) ? Pat0.Read(patch) : null;

        foreach (PatchTarget target in Patch?.Targets ?? [])
        {
            if (target.End > mdls.Length)
                throw new InvalidDataException($"The colour patch of the {name} writes past the end of the model; they do not belong together.");
        }
        PatchedBytesAtOpen = Patch?.Targets.Sum(t => t.Size) ?? 0;
    }

    public string Name { get; }

    /// <summary>The car's colour patch for this part, or null where the part has none (wing, tires, driver).</summary>
    public Pat0? Patch { get; }

    /// <summary>The model's own bytes - what the editor shows and edits.</summary>
    public byte[] Model => _mdls;

    /// <summary>
    /// Puts <paramref name="bytes"/> on the END of the model and returns the offset they landed at. Appending is
    /// the only way anything here may change a length, and it is deliberately the only shape of it: every pointer
    /// inside a model container is an offset from its start, so bytes added after everything else leave all of
    /// them - and every colour patch target - pointing exactly where they did.
    /// </summary>
    public int Append(ReadOnlySpan<byte> bytes, int alignment = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(alignment, 1);

        // A ModelSet2 keeps its relocation table last and says so in its header, so "the end" for it is the end
        // of the BODY: the bytes go in front of the table, which is written out again behind them. That keeps the
        // model the shape every reader expects, however many times it is added to.
        if (Relocations is ModelRelocations table)
        {
            int into = (table.Offset + alignment - 1) & ~(alignment - 1);
            if (Insert(table.Offset, into - table.Offset + bytes.Length))
            {
                bytes.CopyTo(_mdls.AsSpan(into));
                return into;
            }
        }

        int at = (_mdls.Length + alignment - 1) & ~(alignment - 1);
        var grown = new byte[at + bytes.Length];
        _mdls.CopyTo(grown, 0);
        bytes.CopyTo(grown.AsSpan(at));
        _mdls = grown;
        _relocations = null;
        IsModified = true;
        return at;
    }

    /// <summary>
    /// Opens the model up: <paramref name="length"/> zero bytes go in at <paramref name="at"/> and everything
    /// after them moves down, with every pointer past the cut given its new value and the relocation table
    /// written out again. That is how something INSIDE a model gets longer where it already is, instead of a
    /// whole second copy of it going on the end.
    ///
    /// False means this model is not one that can be opened up - a ModelSet1, or a table this cannot reproduce
    /// exactly - and the caller has to fall back to appending. Nothing is changed in that case.
    /// </summary>
    public bool Insert(int at, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(at);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
            return true;

        // A patch target that straddled the cut would end up describing bytes on both sides of it, so the whole
        // move is refused rather than half done. (Targets are palette words inside a set; the cut is at a set's
        // end, so this does not happen in practice - it is here because the cost of being wrong is a broken car.)
        foreach (PatchTarget target in Patch?.Targets ?? [])
        {
            if (target.Offset < at && target.End > at)
                return false;
        }

        if (ModelRelocations.Insert(_mdls, at, length) is not byte[] grown)
            return false;

        _mdls = grown;
        Patch?.Shift(at, length);
        _relocations = null;
        IsModified = true;
        return true;
    }

    /// <summary>
    /// The first byte after <paramref name="offset"/> that something else in the model owns - the nearest thing
    /// any pointer points at, and the relocation table itself. What lies after a model's LAST texture set is not
    /// free space: the header's own tables, the stub headers of its empty texture set slots and the relocation
    /// blob all live there. Null when the model does not say (a ModelSet1), and the caller falls back to its
    /// length.
    /// </summary>
    public int? Limit(int offset) => Relocations?.NextThingAfter(offset, _mdls);

    private ModelRelocations? _relocations;

    private ModelRelocations? Relocations => _relocations ??= ModelRelocations.Read(_mdls);

    public int PaintCount => Patch?.PaintCount ?? 1;
    public bool IsModified { get; private set; }

    public int PatchedBytesAtOpen { get; }

    /// <summary>How many bytes the paints give colours of their own right now.</summary>
    public int PatchedBytes => Patch?.Targets.Sum(t => t.Size) ?? 0;

    /// <summary>Patched bytes that edits have given up so far.</summary>
    public int PatchBytesDropped { get; private set; }

    /// <summary>Reads the model's own bytes - variation 0 unless an edit has given it values of its own.</summary>
    public void ReadBytes(int offset, Span<byte> destination) => ReadBytes(0, offset, destination);

    /// <summary>Reads bytes as one variation sees them.</summary>
    public void ReadBytes(int variation, int offset, Span<byte> destination)
    {
        int paint = Math.Clamp(variation, 0, PaintCount - 1);
        for (int i = 0; i < destination.Length; i++)
        {
            int target = Patch?.FindTarget(offset + i) ?? -1;
            destination[i] = target < 0
                ? _mdls[offset + i]
                : Patch!.GetValue(paint, target)[offset + i - Patch.Targets[target].Offset];
        }
    }

    /// <summary>A range of the model as one variation sees it.</summary>
    public byte[] GetPaintedRange(int variation, int offset, int length)
    {
        byte[] bytes = _mdls.AsSpan(offset, length).ToArray();
        if (Patch is null)
            return bytes;

        int paint = Math.Clamp(variation, 0, PaintCount - 1);
        for (int i = 0; i < Patch.Targets.Count; i++)
        {
            PatchTarget target = Patch.Targets[i];
            int from = Math.Max(target.Offset, offset), to = Math.Min(target.End, offset + length);
            if (from < to)
                Patch.GetValue(paint, i).AsSpan(from - target.Offset, to - from).CopyTo(bytes.AsSpan(from - offset));
        }
        return bytes;
    }

    /// <summary>
    /// Overwrites model bytes, first dropping any colour-patch coverage of the range. Returns the patched bytes
    /// given up, which is what the caller reports to the user.
    /// </summary>
    public int WriteBytes(int offset, ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset + bytes.Length > _mdls.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        int dropped = Patch?.Uncover(offset, bytes.Length) ?? 0;
        bytes.CopyTo(_mdls.AsSpan(offset));
        PatchBytesDropped += dropped;
        IsModified = true;
        return dropped;
    }

    /// <summary>
    /// Writes what ONE variation shows. The range joins the patch if it is not covered yet, so the other
    /// variations keep the bytes they had. Returns the bytes that had to be added to the patch.
    /// </summary>
    public int WriteVariation(int variation, int offset, ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset + bytes.Length > _mdls.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        // A car with one paint has nothing to vary: its "variation 0" is simply the model. Any coverage of the
        // range still has to go, though - a patch that goes on describing bytes that have changed would write
        // the old ones back over this edit when the game loads the car.
        if (Patch is null || PaintCount <= 1)
        {
            PatchBytesDropped += Patch?.Uncover(offset, bytes.Length) ?? 0;
            bytes.CopyTo(_mdls.AsSpan(offset));
            IsModified = true;
            return 0;
        }

        int before = Patch.Targets.Sum(t => t.Size);
        Patch.Cover(offset, bytes.Length, _mdls);
        int paint = Math.Clamp(variation, 0, PaintCount - 1);
        for (int i = 0; i < bytes.Length; i++)
        {
            int target = Patch.FindTarget(offset + i);
            Patch.GetValue(paint, target)[offset + i - Patch.Targets[target].Offset] = bytes[i];
            if (paint == 0)
                _mdls[offset + i] = bytes[i];   // the model is variation 0, the way the originals hold it
        }
        PatchBytesAdded += Patch.Targets.Sum(t => t.Size) - before;
        IsModified = true;
        return Patch.Targets.Sum(t => t.Size) - before;
    }

    /// <summary>Patched bytes that variation edits have added.</summary>
    public int PatchBytesAdded { get; private set; }

    /// <summary>
    /// Puts <paramref name="bytes"/> where a run of <paramref name="length"/> bytes was. A shorter one leaves the
    /// rest blank; a longer one makes the model longer, which is only ever right when nothing lies after the run -
    /// that is the caller's to know, because only it can see what else the model holds.
    /// </summary>
    public void Replace(int offset, int length, ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (bytes.Length <= length)
        {
            bytes.CopyTo(_mdls.AsSpan(offset));
            _mdls.AsSpan(offset + bytes.Length, length - bytes.Length).Clear();
        }
        else
        {
            var grown = new byte[Math.Max(_mdls.Length, offset + bytes.Length)];
            _mdls.CopyTo(grown, 0);
            bytes.CopyTo(grown.AsSpan(offset));
            _mdls = grown;
            _relocations = null;
        }
        IsModified = true;
    }

    /// <summary>
    /// Writes the model's own length into its header, where the container keeps one. A ModelSet2 states it at
    /// 0x10; a ModelSet1 states none at all, and the archive's table is the only record of where it ends. Said
    /// after the model has been made longer, so it does not go on declaring the length it used to be.
    /// </summary>
    public void StateLength()
    {
        if (_mdls.Length >= 0x14 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_mdls) == ModelSet2Magic)
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(_mdls.AsSpan(0x10), _mdls.Length);
    }

    private const uint ModelSet2Magic = 0x534C444D;   // "MDLS"

    /// <summary>
    /// Gives up the colour patch's coverage of a range, without writing anything. Needed before a range MOVES:
    /// a patch target is a byte offset, so one left pointing at bytes that now hold something else would write
    /// its values over whatever moved in. Returns the patched bytes given up.
    /// </summary>
    public int Uncover(int offset, int size)
    {
        int dropped = Patch?.Uncover(offset, size) ?? 0;
        PatchBytesDropped += dropped;
        if (dropped > 0)
            IsModified = true;
        return dropped;
    }

    /// <summary>Marks the model edited when something outside it changed the patch - adding a variation, say.</summary>
    public void Touch() => IsModified = true;

    /// <summary>
    /// Everything in it has been written out, so there is nothing unsaved any more. Until this is said, a model
    /// that was edited once goes on counting as unsaved for as long as it is open - even just after it was saved.
    /// </summary>
    public void MarkSaved() => IsModified = false;

    /// <summary>Whether the paints still give this range colours of their own.</summary>
    public Coverage GetCoverage(int offset, int size)
    {
        if (Patch is null || PaintCount <= 1)
            return Coverage.None;
        int covered = 0;
        for (int i = 0; i < size; i++)
        {
            if (Patch.FindTarget(offset + i) >= 0)
                covered++;
        }
        return covered == 0 ? Coverage.None : covered == size ? Coverage.Full : Coverage.Partial;
    }

    /// <summary>Patched bytes inside a range - what an edit there would cost the paints.</summary>
    public int PatchedBytesIn(int offset, int size)
    {
        if (Patch is null || PaintCount <= 1)
            return 0;
        int covered = 0;
        for (int i = 0; i < size; i++)
        {
            if (Patch.FindTarget(offset + i) >= 0)
                covered++;
        }
        return covered;
    }

    /// <summary>The patch as it should be written back, or null when the part has none.</summary>
    public byte[]? WritePatch(bool standalone = false) => Patch?.Write(standalone);
}
