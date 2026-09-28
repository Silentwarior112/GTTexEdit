using System.Buffers.Binary;

namespace GTTexEdit.Core.Gs;

/// <summary>
/// A PDI GL texture: five raw 64-bit GS registers (tex0, tex1, miptbp1, miptbp2, clamp; 0x28 bytes), bit fields
/// counted from bit 0. Every block pointer in them (TBP0, CBP, mip TBPs) is relative to the set and remapped by
/// the game when the set is uploaded.
/// </summary>
internal struct PgluTexture
{
    public const int Size = 0x28;

    /// <summary>sceGsClamp wrap mode 2: clamp to the MINU..MAXU / MINV..MAXV region, i.e. MAXU/MAXV hold the real size - 1.</summary>
    public const int RegionClamp = 2;

    public ulong Tex0, Tex1, MipTbp1, MipTbp2, Clamp;

    // tex0: TBP0:14 TBW:6 PSM:6 TW:4 TH:4 TCC:1 TFX:2 CBP:14 CPSM:4 CSM:1 CSA:5 CLD:3
    public readonly int Tbp0 => (int)(Tex0 & 0x3FFF);
    public readonly int Tbw => (int)(Tex0 >> 14 & 0x3F);
    public readonly GsPsm Psm => (GsPsm)(Tex0 >> 20 & 0x3F);
    public readonly int Tw => (int)(Tex0 >> 26 & 0xF);
    public readonly int Th => (int)(Tex0 >> 30 & 0xF);
    public readonly int Cbp => (int)(Tex0 >> 37 & 0x3FFF);
    public readonly GsPsm Cpsm => (GsPsm)(Tex0 >> 51 & 0xF);
    public readonly int Csa => (int)(Tex0 >> 56 & 0x1F);
    public readonly int Tcc => (int)(Tex0 >> 34 & 1);
    public readonly int Tfx => (int)(Tex0 >> 35 & 3);
    public readonly int Csm => (int)(Tex0 >> 55 & 1);
    public readonly int Cld => (int)(Tex0 >> 61 & 7);

    // tex1: LCM:1 pad:1 MXL:3 MMAG:1 MMIN:3 MTBA:1 pad:9 L:2 pad:11 K:12
    public readonly int Lcm => (int)(Tex1 & 1);
    public readonly int Mxl => (int)(Tex1 >> 2 & 7);
    public readonly int Mmag => (int)(Tex1 >> 5 & 1);
    public readonly int Mmin => (int)(Tex1 >> 6 & 7);
    public readonly int Mtba => (int)(Tex1 >> 9 & 1);
    public readonly int L => (int)(Tex1 >> 19 & 3);
    public readonly int K => (int)(Tex1 >> 32 & 0xFFF);

    /// <summary>Block pointer and buffer width of mip level 1..6 (miptbp1 holds 1-3, miptbp2 holds 4-6; TBP:14 TBW:6 each).</summary>
    public readonly (int Tbp, int Tbw) GetMip(int level)
    {
        ulong register = level <= 3 ? MipTbp1 : MipTbp2;
        int shift = (level - 1) % 3 * 20;
        return ((int)(register >> shift & 0x3FFF), (int)(register >> shift + 14 & 0x3F));
    }

    // clamp: WMS:2 WMT:2 MINU:10 MAXU:10 MINV:10 MAXV:10
    public readonly int Wms => (int)(Clamp & 3);
    public readonly int Wmt => (int)(Clamp >> 2 & 3);
    public readonly int MinU => (int)(Clamp >> 4 & 0x3FF);
    public readonly int MaxU => (int)(Clamp >> 14 & 0x3FF);
    public readonly int MinV => (int)(Clamp >> 24 & 0x3FF);
    public readonly int MaxV => (int)(Clamp >> 34 & 0x3FF);

    public static PgluTexture Read(ReadOnlySpan<byte> source) => new()
    {
        Tex0 = BinaryPrimitives.ReadUInt64LittleEndian(source),
        Tex1 = BinaryPrimitives.ReadUInt64LittleEndian(source[0x08..]),
        MipTbp1 = BinaryPrimitives.ReadUInt64LittleEndian(source[0x10..]),
        MipTbp2 = BinaryPrimitives.ReadUInt64LittleEndian(source[0x18..]),
        Clamp = BinaryPrimitives.ReadUInt64LittleEndian(source[0x20..]),
    };

    public readonly void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, Tex0);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[0x08..], Tex1);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[0x10..], MipTbp1);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[0x18..], MipTbp2);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[0x20..], Clamp);
    }
}
