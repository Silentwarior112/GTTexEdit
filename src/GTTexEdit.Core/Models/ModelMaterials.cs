using System.Buffers.Binary;
using GTTexEdit.Core.Containers;

namespace GTTexEdit.Core.Models;

/// <summary>A value of a material, by name, at a fixed place in its 0x50-byte record.</summary>
public sealed record MaterialField(string Name, int Offset, bool IsInteger = false)
{
    public override string ToString() => Name;
}

/// <summary>
/// A paint finish: what the original cars write into a body material for one kind of paint. Measured over every
/// GT4 menu car, the three most common patched material states are exactly these (2,082 / 992 / 313 occurrences),
/// e.g. hond0036: white and red are Gloss; silver metallic, black pearl and blue pearl are all Metallic - with the
/// specular highlight on some of its body materials and without it on the others.
/// A preset sets diffuse RGB, specular RGB and the specular power; everything else is left alone.
/// </summary>
public sealed record MaterialPreset(string Name, float Diffuse, float Specular, float SpecularPower)
{
    public static readonly IReadOnlyList<MaterialPreset> All =
    [
        new("Gloss (solid paint)", 1f, 0f, 0f),
        new("Metallic / pearl", 0.5f, 1.5f, 16f),
        new("Metallic / pearl, no highlight", 0.5f, 0f, 16f),
    ];

    public override string ToString() => $"{Name}   -   diffuse {Diffuse:0.##}, specular {Specular:0.##}, power {SpecularPower:0.##}";
}

/// <summary>
/// The materials of a model container. PGLUmaterial is the same 0x50 bytes in both engines - four RGBA colours as
/// floats, then four scalars - but the two engines vary them differently: a GT4 car keeps ONE array and lets the
/// colour patch write different values into it per paint, while GT3 stores a COMPLETE array per variation, next to
/// the base one. Both come out of here as a list of arrays plus the offsets to read them at.
/// </summary>
internal sealed class ModelMaterials
{
    public const int Size = 0x50;

    /// <summary>PGLUmaterial: four RGBA colours as floats, then four scalars.</summary>
    public static readonly IReadOnlyList<MaterialField> Fields = Build();

    public int Count { get; }

    /// <summary>Model offset of the first material of each array: [0] is the base one, the rest are GT3 variations.</summary>
    public IReadOnlyList<int> Arrays { get; }

    /// <summary>
    /// Model offset of the GT3 table of per-variation array pointers, or -1. Entry v says where variation v reads
    /// its materials, and SEVERAL VARIATIONS OFTEN NAME THE SAME ARRAY: measured over the 290 GT3 menu cars, 161
    /// models share one between two or more variations (ty0028: variations 0, 2, 3 and 4 all read 0x2f0). Editing
    /// through one of them therefore edits all of them, until the variation is given an array of its own.
    /// </summary>
    public int VariationTable { get; } = -1;

    /// <summary>How many entries that table has.</summary>
    public int VariationCount { get; }

    public ModelMaterials(byte[] model)
    {
        ArgumentNullException.ThrowIfNull(model);
        uint magic = model.Length >= 0x40 ? BinaryPrimitives.ReadUInt32LittleEndian(model) : 0;
        var arrays = new List<int>();

        switch (magic)
        {
            case ModelSetTextures.ModelSet2Magic:
                Count = U16(model, 0x1A);
                Add(arrays, model, I32(model, 0x40), Count);
                break;

            case ModelSetTextures.ModelSet1Magic:
            {
                Count = U16(model, 0x14);
                int baseArray = I32(model, 0x28);
                Add(arrays, model, baseArray, Count);

                int variations = U16(model, 0x1A), table = I32(model, 0x38);
                if (arrays.Count == 1 && variations > 0 && table > 0 && table + variations * 4 <= model.Length)
                {
                    VariationTable = table;
                    VariationCount = variations;
                    // One entry per variation, always: a variation's array is Arrays[variation + 1], and that has
                    // to hold even if one of the pointers is unusable, or every later variation would be read
                    // through the wrong array.
                    for (int i = 0; i < variations; i++)
                    {
                        int offset = I32(model, table + i * 4);
                        arrays.Add(Fits(model, offset, Count) ? offset : baseArray);
                    }
                }
                break;
            }
        }

        Arrays = arrays;
        if (arrays.Count == 0)
            Count = 0;
    }

    private static bool Fits(byte[] model, int offset, int count) =>
        count > 0 && offset > 0 && offset + (long)count * Size <= model.Length;

    private static void Add(List<int> arrays, byte[] model, int offset, int count)
    {
        if (Fits(model, offset, count))
            arrays.Add(offset);
    }

    private static int U16(byte[] data, int at) => at >= 0 && at + 2 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) : 0;

    private static int I32(byte[] data, int at) => at >= 0 && at + 4 <= data.Length ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)) : 0;

    private static List<MaterialField> Build()
    {
        var fields = new List<MaterialField>();
        string[] colours = ["Ambient", "Diffuse", "Specular", "Colour 4"];
        for (int c = 0; c < colours.Length; c++)
            for (int channel = 0; channel < 4; channel++)
                fields.Add(new MaterialField($"{colours[c]} {"RGBA"[channel]}", c * 16 + channel * 4));

        fields.Add(new MaterialField("Specular power", 0x40));
        fields.Add(new MaterialField("Flags", 0x44, IsInteger: true));
        fields.Add(new MaterialField("Value 2", 0x48));
        fields.Add(new MaterialField("Value 3", 0x4C));
        return fields;
    }

    /// <summary>Which value of a material a paint finish sets, or null when it leaves it alone.</summary>
    public static float? PresetValue(MaterialPreset preset, MaterialField field) => field.Offset switch
    {
        0x10 or 0x14 or 0x18 => preset.Diffuse,
        0x20 or 0x24 or 0x28 => preset.Specular,
        0x40 => preset.SpecularPower,
        _ => null,
    };
}
