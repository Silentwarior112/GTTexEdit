using System.Buffers.Binary;

namespace GTTexEdit.Core.Models;

/// <summary>
/// The relocation table of a ModelSet2 ("MDLS"), and the one thing it makes possible: opening the model up in the
/// middle so something inside it can get longer where it already is.
///
/// A model is loaded at whatever address the game has free, so every pointer in it is an offset from the model's
/// start that the engine adds that address to. The table is the list of where those pointers are. Its blob sits at
/// the very end, at header 0x04 with its length at 0x08 (and 0x04 + 0x08 == 0x10, the model's length):
///
///   u32 offset of the user chunks (blob-relative)   u32 0xE4859D6D
///   groups until a zero type: type, symbol, count, first location, then the gaps between the rest of them -
///     a byte 0x80+n meaning "n more, each the same gap", or a byte n-1 meaning "n gaps, listed"
///   a zero byte, then 8 zero bytes (two empty user chunks)
///
/// Gaps and counts are 7-bit big-endian numbers: one byte under 0x80, two with 0x80 set, four with 0xC0 set.
///
/// Measured over all 8,053 models of the 1,613 GT4 car files (research/notes/mdls-container.md): every one is a
/// single group of type 3, symbol 0; the locations ascend, are 4-aligned and start at 0x38; NO location ever lies
/// inside a texture set, so a Tex1 holds no pointers of its own; and re-encoding the parsed locations reproduces
/// all 8,053 blobs byte for byte. That last one is what lets this be written back rather than patched.
/// </summary>
internal sealed class ModelRelocations
{
    private const uint ModelSet2Magic = 0x534C444D;   // "MDLS"
    private const uint TableMagic = 0xE4859D6D;
    private const int PointerGroup = 3;               // the only group type the car files use: u32 pointers

    private ModelRelocations(int offset, int size, IReadOnlyList<int> locations)
    {
        Offset = offset;
        Size = size;
        Locations = locations;
    }

    /// <summary>Where the blob sits in the model - header 0x04.</summary>
    public int Offset { get; }

    /// <summary>How long the blob is - header 0x08.</summary>
    public int Size { get; }

    /// <summary>Every place in the model that holds a pointer, ascending.</summary>
    public IReadOnlyList<int> Locations { get; }

    /// <summary>
    /// Reads the table of a ModelSet2. Returns null for anything this cannot put back exactly as it found it -
    /// another container, a header that disagrees with itself, or a shape of table the car files never use - so
    /// that a caller can fall back to a way of growing that does not need the table at all.
    /// </summary>
    public static ModelRelocations? Read(ReadOnlySpan<byte> model)
    {
        if (model.Length < 0x14 || BinaryPrimitives.ReadUInt32LittleEndian(model) != ModelSet2Magic)
            return null;

        int offset = BinaryPrimitives.ReadInt32LittleEndian(model[0x04..]);
        int size = BinaryPrimitives.ReadInt32LittleEndian(model[0x08..]);
        int length = BinaryPrimitives.ReadInt32LittleEndian(model[0x10..]);
        if (BinaryPrimitives.ReadInt32LittleEndian(model[0x0C..]) != 0)
            return null;                                    // a relocation base this does not know how to keep
        if (offset <= 0 || size <= 0 || offset + size != length || length > model.Length)
            return null;

        if (BinaryPrimitives.ReadUInt32LittleEndian(model[(offset + 4)..]) != TableMagic)
            return null;

        var locations = new List<int>();
        int at = offset + 8;
        while (true)
        {
            if (at >= offset + size)
                return null;
            int type = Read7(model, ref at);
            if (type == 0)
                break;
            if (type != PointerGroup)
                return null;

            int symbol = Read7(model, ref at);
            int count = Read7(model, ref at);
            int first = Read7(model, ref at);
            if (symbol != 0 || count <= 0)
                return null;

            locations.Add(first);
            int current = first;
            for (int left = count - 1; left > 0;)
            {
                if (at >= offset + size)
                    return null;
                int control = model[at++];
                if (control >= 0x80)
                {
                    int run = control - 0x7F;
                    int gap = Read7(model, ref at);
                    if (run > left)
                        return null;
                    for (int i = 0; i < run; i++)
                    {
                        current += gap;
                        locations.Add(current);
                    }
                    left -= run;
                }
                else
                {
                    int run = control + 1;
                    if (run > left)
                        return null;
                    for (int i = 0; i < run; i++)
                    {
                        current += Read7(model, ref at);
                        locations.Add(current);
                    }
                    left -= run;
                }
            }
        }

        // Only a table this can write back again is any use: one group, and every location a whole pointer inside
        // the model. Anything else is left alone rather than half-understood.
        for (int i = 0; i < locations.Count; i++)
        {
            if (locations[i] < 0 || locations[i] + 4 > offset || (locations[i] & 3) != 0)
                return null;
            if (i > 0 && locations[i] <= locations[i - 1])
                return null;
        }

        // Writing it out again has to give back exactly the bytes that were read, or this does not understand the
        // table well enough to be allowed to rewrite it.
        var table = new ModelRelocations(offset, size, locations);
        return table.Write().AsSpan().SequenceEqual(model.Slice(offset, size)) ? table : null;
    }

    /// <summary>The blob as the model holds it, built again from the locations.</summary>
    public byte[] Write() => Write(Locations);

    /// <summary>Builds the blob for a list of ascending pointer locations.</summary>
    public static byte[] Write(IReadOnlyList<int> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        if (locations.Count == 0)
            throw new ArgumentException("A relocation table needs at least one pointer.", nameof(locations));

        var stream = new List<byte>();
        Write7(stream, PointerGroup);
        Write7(stream, 0);
        Write7(stream, locations.Count);
        Write7(stream, locations[0]);

        var pending = new List<int>();
        void Flush()
        {
            for (int from = 0; from < pending.Count; from += 128)
            {
                int take = Math.Min(128, pending.Count - from);
                stream.Add((byte)(take - 1));
                for (int i = 0; i < take; i++)
                    Write7(stream, pending[from + i]);
            }
            pending.Clear();
        }

        for (int i = 1; i < locations.Count;)
        {
            int gap = locations[i] - locations[i - 1];
            int run = 1;
            while (i + run < locations.Count && locations[i + run] - locations[i + run - 1] == gap && run < 128)
                run++;

            // three or more of the same gap pay for themselves as a run; fewer go in the list of gaps
            if (run >= 3)
            {
                Flush();
                stream.Add((byte)(0x7F + run));
                Write7(stream, gap);
            }
            else
            {
                for (int k = 0; k < run; k++)
                    pending.Add(gap);
            }
            i += run;
        }
        Flush();
        stream.Add(0);                      // the group terminator

        var blob = new byte[8 + stream.Count + 8];
        BinaryPrimitives.WriteInt32LittleEndian(blob, 8 + stream.Count);     // where the (empty) user chunks start
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), TableMagic);
        stream.CopyTo(blob, 8);
        return blob;
    }

    /// <summary>
    /// Makes a model longer at <paramref name="at"/> by <paramref name="length"/> zero bytes, so that whatever
    /// ends there has room to grow. Everything after the cut moves down: every pointer that pointed past it is
    /// given its new value, every pointer that LIVES past it is listed at its new place, and the table is written
    /// out again. Returns the new model, or null when the model is not one this understands well enough to do it.
    /// </summary>
    public static byte[]? Insert(byte[] model, int at, int length)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
            return model;
        if (Read(model) is not ModelRelocations table)
            return null;
        if (at < 0 || at > table.Offset)
            return null;                    // the cut has to be in the model's body, not in the table itself

        var moved = new List<int>(table.Locations.Count);
        foreach (int location in table.Locations)
        {
            if (location + 4 > at && location < at)
                return null;                // a pointer straddling the cut: nothing here can say what that means
            moved.Add(location >= at ? location + length : location);
        }

        byte[] blob = Write(moved);
        var grown = new byte[table.Offset + length + blob.Length];
        model.AsSpan(0, at).CopyTo(grown);
        model.AsSpan(at, table.Offset - at).CopyTo(grown.AsSpan(at + length));
        blob.CopyTo(grown.AsSpan(table.Offset + length));

        // the pointers themselves: a value past the cut has moved by exactly as much as the cut is wide
        for (int i = 0; i < moved.Count; i++)
        {
            int value = BinaryPrimitives.ReadInt32LittleEndian(grown.AsSpan(moved[i]));
            if (value >= at && value != 0)
                BinaryPrimitives.WriteInt32LittleEndian(grown.AsSpan(moved[i]), value + length);
        }

        BinaryPrimitives.WriteInt32LittleEndian(grown.AsSpan(0x04), table.Offset + length);
        BinaryPrimitives.WriteInt32LittleEndian(grown.AsSpan(0x08), blob.Length);
        BinaryPrimitives.WriteInt32LittleEndian(grown.AsSpan(0x10), grown.Length);
        return grown;
    }

    /// <summary>
    /// The first thing in the model that lies after <paramref name="offset"/> and may not be written over: the
    /// nearest place any pointer points to, and failing that the table itself. It is what a set may grow into
    /// before something has to move - the bytes after a model's last texture set are NOT free, they hold the
    /// tables the header points at and the relocation blob.
    /// </summary>
    public int NextThingAfter(int offset, ReadOnlySpan<byte> model)
    {
        int next = Offset;
        foreach (int location in Locations)
        {
            int value = BinaryPrimitives.ReadInt32LittleEndian(model[location..]);
            if (value > offset && value < next)
                next = value;
        }
        return next;
    }

    private static int Read7(ReadOnlySpan<byte> data, ref int at)
    {
        byte b = data[at++];
        if ((b & 0x80) == 0)
            return b;
        if ((b & 0x40) != 0)
        {
            int wide = ((b & 0x3F) << 24) | (data[at] << 16) | (data[at + 1] << 8) | data[at + 2];
            at += 3;
            return wide;
        }
        int value = ((b & 0x7F) << 8) | data[at];
        at += 1;
        return value;
    }

    private static void Write7(List<byte> into, int value)
    {
        if (value < 0x80)
        {
            into.Add((byte)value);
        }
        else if (value < 0x4000)
        {
            into.Add((byte)((value >> 8) | 0x80));
            into.Add((byte)value);
        }
        else
        {
            into.Add((byte)((value >> 24) | 0xC0));
            into.Add((byte)(value >> 16));
            into.Add((byte)(value >> 8));
            into.Add((byte)value);
        }
    }
}
