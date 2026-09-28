using System.Buffers.Binary;
using GTTexEdit.Core.Models;

namespace GTTexEdit.Core.Editing;

/// <summary>
/// Removing and reordering the variations of a GT3 model, in place.
///
/// GT3 does not diff bytes the way a GT4 colour patch does. It stores, per variation, a whole palette for every
/// texture (picked by the clut patch table at the end of each Tex1) and a whole array of materials (picked by the
/// variation table in the ModelSet1 header). Both are reached through arrays of pointers, so DROPPING one is just a
/// shorter array and SWAPPING two is just two pointers - neither needs a byte more than the model already has.
/// Adding one would: it needs a palette and a material array that are not there, which is why the editor says no.
///
/// The bytes a dropped variation leaves behind stay where they are, unreferenced. The model keeps its length, so the
/// archive around it is still written back where it was.
/// </summary>
internal static class VariationTables
{
    public static void Remove(TexturePart part, int variation) => Edit(part, table => Drop(table, variation));

    public static void Move(TexturePart part, int from, int to) => Edit(part, table => Swap(table, from, to));

    private static void Edit(TexturePart part, Action<Table> edit)
    {
        byte[] model = part.Model.Model;
        if (BinaryPrimitives.ReadUInt32LittleEndian(model) != ModelSetTextures.ModelSet1Magic)
            return;

        // the clut patch table of every texture set that has one: a count, then one pointer per variation
        foreach (TextureSet set in part.Sets)
        {
            if (set.ClutPatchTableOffset >= 0)
                edit(new Table(model, set.ClutPatchTableOffset, set.ClutPatchTableOffset + 4, CountIsU32: true));
        }

        // and the variation material table in the model's own header, whose count sits apart from it
        int count = BinaryPrimitives.ReadUInt16LittleEndian(model.AsSpan(0x1A));
        int entries = BinaryPrimitives.ReadInt32LittleEndian(model.AsSpan(0x38));
        if (count > 0 && entries > 0 && entries + count * 4 <= model.Length)
            edit(new Table(model, 0x1A, entries, CountIsU32: false));

        part.Model.Touch();
    }

    /// <summary>A count somewhere, and the array of pointers it counts.</summary>
    private readonly record struct Table(byte[] Model, int CountAt, int EntriesAt, bool CountIsU32)
    {
        public int Count
        {
            get => CountIsU32
                ? BinaryPrimitives.ReadInt32LittleEndian(Model.AsSpan(CountAt))
                : BinaryPrimitives.ReadUInt16LittleEndian(Model.AsSpan(CountAt));
            set
            {
                if (CountIsU32)
                    BinaryPrimitives.WriteInt32LittleEndian(Model.AsSpan(CountAt), value);
                else
                    BinaryPrimitives.WriteUInt16LittleEndian(Model.AsSpan(CountAt), (ushort)value);
            }
        }

        public bool Holds(int index) => index >= 0 && index < Count && EntriesAt + (Count * 4) <= Model.Length;

        public int this[int index]
        {
            get => BinaryPrimitives.ReadInt32LittleEndian(Model.AsSpan(EntriesAt + index * 4));
            set => BinaryPrimitives.WriteInt32LittleEndian(Model.AsSpan(EntriesAt + index * 4), value);
        }
    }

    private static void Drop(Table table, int index)
    {
        if (!table.Holds(index) || table.Count <= 1)
            return;
        for (int i = index; i < table.Count - 1; i++)
            table[i] = table[i + 1];
        table.Count--;
    }

    private static void Swap(Table table, int from, int to)
    {
        if (!table.Holds(from) || !table.Holds(to) || from == to)
            return;
        (table[from], table[to]) = (table[to], table[from]);
    }
}
