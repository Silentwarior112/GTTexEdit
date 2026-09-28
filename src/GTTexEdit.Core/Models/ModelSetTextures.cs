using System.Buffers.Binary;
using GTTexEdit.Core.Containers;

namespace GTTexEdit.Core.Models;

/// <summary>Where one Tex1 texture set sits inside a model container.</summary>
/// <param name="List">
/// Which table it came through: a ModelSet2's texture set list, or a ModelSet1's variation (0 = the base sets).
/// </param>
/// <param name="Slot">Slot within that table.</param>
/// <param name="PointerOffset">Offset of the u32 that points at the set, relative to the model.</param>
/// <param name="Offset">Offset of the "Tex1" magic, relative to the model.</param>
/// <param name="Size">The set's own size field (0 for an empty placeholder set).</param>
internal sealed record TextureSetLocation(int List, int Slot, int PointerOffset, int Offset, int Size, int TextureCount, string Label)
{
    public TextureSetLocation(int list, int slot, int pointerOffset, int offset, int size, int textureCount)
        : this(list, slot, pointerOffset, offset, size, textureCount, $"{list}.{slot}")
    {
    }
}

/// <summary>
/// The texture side of a model container, for both engines.
///
/// ModelSet2 ("MDLS", GT4): counts at 0x16 (models, shapes, materials, texture sets, texture set lists), the list
/// table at 0x44. A list is u32 offsets to its sets; unused slots still point at an empty Tex1 header.
///
/// ModelSet1 ("GTM1", GT3): counts at 0x10 (models, shapes, materials, texture sets, variation texture sets,
/// variation materials), the set table at 0x2C and the variation table at 0x34. GT3 does not patch palettes the
/// way GT4 does - it stores a COMPLETE set of textures per car variation, so each variation shows up here as its
/// own list of sets.
/// </summary>
internal sealed class ModelSetTextures
{
    public const uint ModelSet2Magic = 0x534C444D; // "MDLS"
    public const uint ModelSet1Magic = 0x314D5447; // "GTM1"

    public ContentKind Kind { get; }
    public int ModelCount { get; }
    public int ShapeCount { get; }
    public int MaterialCount { get; }
    public int TextureSetCount { get; }
    public int ListCount { get; }
    public IReadOnlyList<TextureSetLocation> Sets { get; }

    public static bool IsModelSet(ReadOnlySpan<byte> data) =>
        data.Length >= 0x40 && BinaryPrimitives.ReadUInt32LittleEndian(data) is ModelSet2Magic or ModelSet1Magic;

    public ModelSetTextures(byte[] model)
    {
        ArgumentNullException.ThrowIfNull(model);
        uint magic = model.Length >= 0x40 ? BinaryPrimitives.ReadUInt32LittleEndian(model) : 0;
        var sets = new List<TextureSetLocation>();

        switch (magic)
        {
            case ModelSet2Magic:
            {
                Kind = ContentKind.ModelSet2;
                ModelCount = U16(model, 0x16);
                ShapeCount = U16(model, 0x18);
                MaterialCount = U16(model, 0x1A);
                TextureSetCount = U16(model, 0x1C);
                ListCount = U16(model, 0x1E);
                int listsOffset = I32(model, 0x44);

                for (int list = 0; list < ListCount; list++)
                {
                    int entries = I32(model, listsOffset + list * 4);
                    for (int slot = 0; slot < TextureSetCount; slot++)
                        Add(sets, model, list, slot, entries + slot * 4, $"{list}.{slot}");
                }
                break;
            }

            case ModelSet1Magic:
            {
                Kind = ContentKind.ModelSet1;
                ModelCount = U16(model, 0x10);
                ShapeCount = U16(model, 0x12);
                MaterialCount = U16(model, 0x14);
                TextureSetCount = U16(model, 0x16);
                int variations = U16(model, 0x18);
                ListCount = 1 + variations;
                int setTable = I32(model, 0x2C), variationTable = I32(model, 0x34);

                for (int slot = 0; slot < TextureSetCount; slot++)
                    Add(sets, model, 0, slot, setTable + slot * 4, $"set {slot}");

                // Each variation is a complete alternative set of textures - GT3's own way of doing what a GT4
                // colour patch does by swapping palettes.
                for (int variation = 0; variation < variations && variationTable > 0; variation++)
                {
                    int table = I32(model, variationTable + variation * 4);
                    if (table <= 0 || table + TextureSetCount * 4 > model.Length)
                        continue;
                    for (int slot = 0; slot < TextureSetCount; slot++)
                        Add(sets, model, variation + 1, slot, table + slot * 4, $"variation {variation + 1} set {slot}");
                }
                break;
            }

            default:
                throw new InvalidDataException("Not a model container (neither the \"MDLS\" nor the \"GTM1\" magic is there).");
        }

        Sets = sets;
    }

    private static void Add(List<TextureSetLocation> sets, byte[] model, int list, int slot, int pointer, string label)
    {
        if (pointer < 0 || pointer + 4 > model.Length)
            return;
        int offset = I32(model, pointer);
        if (offset <= 0 || offset + 0x30 > model.Length || BinaryPrimitives.ReadUInt32LittleEndian(model.AsSpan(offset)) != Gs.Tex1Reader.Magic)
            return;

        sets.Add(new TextureSetLocation(list, slot, pointer, offset,
            I32(model, offset + 0x0C), U16(model, offset + 0x14), label));
    }

    private static int U16(byte[] data, int at) => at >= 0 && at + 2 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) : 0;

    private static int I32(byte[] data, int at) => at >= 0 && at + 4 <= data.Length ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)) : 0;
}
