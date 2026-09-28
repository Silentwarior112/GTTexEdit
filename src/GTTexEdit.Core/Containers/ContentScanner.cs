using System.Buffers.Binary;

namespace GTTexEdit.Core.Containers;

/// <summary>What a run of bytes turned out to be.</summary>
public enum ContentKind
{
    Unknown,

    /// <summary>"CAR4" - a GT4 car: models, colour patches and the rest in one container.</summary>
    Car4,

    /// <summary>"MDLS" - ModelSet2, the GT4 model container.</summary>
    ModelSet2,

    /// <summary>"GTM1" - ModelSet1, the GT3 model container. Holds a complete texture set per car variation.</summary>
    ModelSet1,

    /// <summary>"Tex1" - a texture set on its own, as course archives carry them.</summary>
    TextureSet,

    /// <summary>A table of absolute offsets followed by the files it points at: course archives, GT3 cars.</summary>
    Archive,
}

/// <summary>One thing found inside a file: where it is, what it is, and what to call it.</summary>
public sealed record Constituent(ContentKind Kind, int Offset, int Length, int Slot, string Name)
{
    public override string ToString() => $"{Name} ({Kind}, {Length / 1024.0:0.#} KB)";
}

/// <summary>
/// Works out what a file holds. Cars, models and texture sets announce themselves with a magic; the archives do
/// not - a course archive is only a table of absolute offsets, zero where a slot is empty, each entry running to
/// the next one. So an archive is recognised by the shape of that table and then walked, and anything inside it
/// that is not itself a known magic is searched for the magics it might be hiding.
/// </summary>
public static class ContentScanner
{
    private const uint Car4Magic = 0x34524143;
    private const uint ModelSet2Magic = 0x534C444D;
    private const uint ModelSet1Magic = 0x314D5447;
    private const uint TextureSetMagic = 0x31786554;

    /// <summary>Alignment every constituent of an archive is on.</summary>
    private const int Alignment = 0x40;

    /// <summary>Step of the blind magic search. GT3 wheel and tire files hold theirs on a finer grid than 0x40.</summary>
    private const int SearchStep = 0x10;

    // Slot names from the course archive splitters (Misuka's GT3/GT4 course archive editors). The header size says
    // which game's table applies: GT4 courses have 64 slots, GT3 courses 48, a GT3 car 16.
    private static readonly Dictionary<int, string> Gt4CourseSlots = new()
    {
        [1] = "main course model", [2] = "main course visualiser", [3] = "LOD mirror visualiser", [4] = "LOD 2P visualiser",
        [5] = "environment model", [6] = "environment visualiser", [7] = "course visualiser", [8] = "visualiser",
        [9] = "reflection model", [10] = "reflection visualiser", [13] = "reflection mask model", [14] = "reflection mask visualiser",
        [17] = "after model", [18] = "after visualiser", [19] = "visualiser 2", [20] = "visualiser 3",
        [21] = "skybox model", [22] = "course background model", [23] = "environment sky model", [24] = "LOD skybox model",
        [25] = "course file", [26] = "smoke texture", [27] = "course model", [28] = "course runway data", [29] = "course EFX",
        [30] = "billboard data", [31] = "course file 2", [32] = "environment parameters", [33] = "course map data",
        [34] = "unused texture", [35] = "course sound data", [37] = "flare shape", [38] = "flare texture",
        [39] = "particle texture", [40] = "flare reflection texture", [41] = "replay data", [42] = "rally logo texture",
        [43] = "projection mask model", [44] = "replay data", [45] = "play start camera", [46] = "replay start camera",
        [47] = "gadgets and spawns", [48] = "course preview camera", [49] = "course logo texture", [50] = "model",
        [51] = "photo mode data", [52] = "start camera", [53] = "replay start camera 2", [54] = "FG sky model", [55] = "runway data",
    };

    private static readonly Dictionary<int, string> Gt3CourseSlots = new()
    {
        [1] = "main course model", [2] = "main course visualiser", [3] = "LOD course visualiser",
        [4] = "car reflection visualiser", [5] = "car reflection model", [6] = "course reflection visualiser",
        [7] = "extra course visualiser", [9] = "road surface model", [10] = "road surface visualiser",
        [13] = "road surface model 2", [14] = "road surface visualiser 2", [21] = "skybox model",
        [22] = "course background model", [23] = "car reflection model 2", [24] = "LOD skybox model",
        [25] = "course file", [26] = "smoke texture", [27] = "extra course model", [28] = "course runway data",
        [29] = "light flare data", [30] = "billboard data", [31] = "course parameters", [32] = "environment parameters",
        [33] = "course map data", [34] = "replay cameras", [35] = "course sound data", [36] = "pylon models",
        [37] = "course file 3", [38] = "course file 4", [39] = "course file 5", [40] = "course file 6",
        [41] = "camera 1", [42] = "camera 2", [43] = "course model",
    };

    private static readonly Dictionary<int, string> Gt3CarSlots = new()
    {
        [1] = "car info", [2] = "model", [3] = "tires", [4] = "wheels", [5] = "padding",
    };

    /// <summary>What the bytes start with, judged by magic and a look at the header behind it.</summary>
    public static ContentKind Identify(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x10)
            return ContentKind.Unknown;

        return BinaryPrimitives.ReadUInt32LittleEndian(data) switch
        {
            Car4Magic => ContentKind.Car4,
            ModelSet2Magic when IsModelSet2(data) => ContentKind.ModelSet2,
            ModelSet1Magic when IsModelSet1(data) => ContentKind.ModelSet1,
            TextureSetMagic when IsTextureSet(data) => ContentKind.TextureSet,
            _ => LooksLikeArchive(data, out _) ? ContentKind.Archive : ContentKind.Unknown,
        };
    }

    /// <summary>
    /// Whether the bytes open with an archive's offset table: a run of u32 slots, zero where empty, the first
    /// non-zero one pointing at the end of the table itself.
    /// </summary>
    public static bool LooksLikeArchive(ReadOnlySpan<byte> data, out int headerSize)
    {
        headerSize = 0;
        if (data.Length < 0x40 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0)
            return false;

        // The first slot that is used points at the end of the table, because the files start right behind it.
        int first = 4;
        for (; first < Math.Min(data.Length, 0x400); first += 4)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(data[first..]);
            if (value != 0)
            {
                headerSize = (int)value;
                break;
            }
        }
        if (headerSize < first + 4 || headerSize > 0x400 || headerSize % 4 != 0 || headerSize >= data.Length)
            return false;

        // every other slot is either empty or points inside the file, at the same alignment
        int present = 0;
        for (int slot = 0; slot * 4 < headerSize; slot++)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(data[(slot * 4)..]);
            if (value == 0)
                continue;
            if (value < (uint)headerSize || value > (uint)data.Length || value % Alignment != 0)
                return false;
            present++;
        }
        return present >= 2;
    }

    /// <summary>
    /// Everything in the file worth opening. A known magic is the whole answer; an archive is walked slot by slot,
    /// and any slot that is not a magic of its own is searched for the ones it might contain.
    /// </summary>
    public static IReadOnlyList<Constituent> Scan(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var found = new List<Constituent>();

        ContentKind kind = Identify(data);
        if (kind == ContentKind.Archive)
        {
            LooksLikeArchive(data, out int headerSize);
            Dictionary<int, string> names = headerSize switch
            {
                0x100 => Gt4CourseSlots,
                0xC0 => Gt3CourseSlots,
                0x40 => Gt3CarSlots,
                _ => [],
            };

            foreach (var (slot, offset, length) in Entries(data, headerSize))
            {
                string name = names.TryGetValue(slot, out string? known) ? $"{slot:00} {known}" : $"{slot:00}";
                ContentKind inner = Identify(data.AsSpan(offset, length));
                if (inner is ContentKind.ModelSet2 or ContentKind.ModelSet1 or ContentKind.TextureSet or ContentKind.Car4)
                    found.Add(new Constituent(inner, offset, length, slot, name));
                else
                    found.AddRange(Search(data, offset, length, slot, name));
            }
            return found;
        }

        if (kind != ContentKind.Unknown)
            return [new Constituent(kind, 0, data.Length, 0, Describe(kind))];

        return Search(data, 0, data.Length, 0, "");
    }

    /// <summary>The non-empty slots of an archive: each runs to the next one that is used.</summary>
    private static IEnumerable<(int Slot, int Offset, int Length)> Entries(byte[] data, int headerSize)
    {
        var offsets = new List<(int Slot, int Offset)>();
        for (int slot = 0; slot * 4 < headerSize; slot++)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(slot * 4));
            if (offset != 0)
                offsets.Add((slot, offset));
        }

        int[] ends = [.. offsets.Select(o => o.Offset).Order(), data.Length];
        foreach (var (slot, offset) in offsets)
        {
            int end = ends.First(e => e > offset);
            if (end > offset)
                yield return (slot, offset, end - offset);
        }
    }

    // A blind search for the magics, on the alignment every one of these files is written to. Each hit is validated
    // by its own header, so a stray four bytes in the middle of some vertex data does not become a texture set.
    // A texture set states its own length; a model container does not, so it runs to whatever was found next.
    private static List<Constituent> Search(byte[] data, int from, int length, int slot, string name)
    {
        int end = Math.Min(data.Length, from + length);
        var hits = new List<(int At, ContentKind Kind, int Size)>();
        int claimed = from;

        for (int at = from; at + 0x30 <= end; at += SearchStep)
        {
            if (at < claimed)
                continue;
            ReadOnlySpan<byte> here = data.AsSpan(at, end - at);
            ContentKind kind = BinaryPrimitives.ReadUInt32LittleEndian(here) switch
            {
                ModelSet2Magic when IsModelSet2(here) => ContentKind.ModelSet2,
                ModelSet1Magic when IsModelSet1(here) => ContentKind.ModelSet1,
                TextureSetMagic when IsTextureSet(here) => ContentKind.TextureSet,
                _ => ContentKind.Unknown,
            };
            if (kind == ContentKind.Unknown)
                continue;

            // A texture set states its own length. A model container does not, so it is measured by how far its
            // own texture sets reach - otherwise the sets INSIDE it would be mistaken for the next thing along.
            int size = kind == ContentKind.TextureSet
                ? Align(BinaryPrimitives.ReadInt32LittleEndian(here[0x0C..]))
                : ModelExtent(data, at, end - at);
            hits.Add((at, kind, size));
            if (size > 0)
                claimed = at + size;
        }

        // Each hit runs to the next one, or to the end of what it was found in. The measured extent above is only
        // used to decide which hits are INSIDE a model and should not be reported at all; it must not decide how
        // long the model is, because it measures the model by its texture sets and a model can hold things after
        // them - a wheel whose variation materials sit past its texture set, for one.
        var found = new List<Constituent>();
        for (int i = 0; i < hits.Count; i++)
        {
            var (at, kind, _) = hits[i];
            int until = i + 1 < hits.Count ? hits[i + 1].At : end;
            found.Add(new Constituent(kind, at, until - at, slot,
                (name.Length == 0 ? "" : name + " - ") + Describe(kind) + (hits.Count > 1 ? $" {i + 1}" : "")));
        }
        return found;
    }

    /// <summary>How far a model container reaches, judged by the texture sets it points at. 0 when it will not parse.</summary>
    private static int ModelExtent(byte[] data, int at, int available)
    {
        try
        {
            byte[] model = data.AsSpan(at, available).ToArray();
            var sets = new Models.ModelSetTextures(model);
            int extent = 0;
            foreach (var set in sets.Sets)
                extent = Math.Max(extent, set.Offset + Math.Max(set.Size, 0x40));
            return extent > 0 && extent <= available ? Align(extent) : 0;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return 0;
        }
    }

    private static int Align(int value) => (value + Alignment - 1) & ~(Alignment - 1);

    public static string Describe(ContentKind kind) => kind switch
    {
        ContentKind.Car4 => "GT4 car",
        ContentKind.ModelSet2 => "ModelSet2",
        ContentKind.ModelSet1 => "ModelSet1",
        ContentKind.TextureSet => "texture set",
        ContentKind.Archive => "archive",
        _ => "unknown",
    };

    // ------------------------------------------------------------------------------------------- validation

    private static bool IsModelSet2(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x80)
            return false;
        int lists = BinaryPrimitives.ReadUInt16LittleEndian(data[0x1E..]);
        int listsOffset = BinaryPrimitives.ReadInt32LittleEndian(data[0x44..]);
        return lists > 0 && lists < 0x400 && listsOffset > 0 && listsOffset + lists * 4 <= data.Length;
    }

    private static bool IsModelSet1(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x40)
            return false;
        int sets = BinaryPrimitives.ReadUInt16LittleEndian(data[0x16..]);
        int setsOffset = BinaryPrimitives.ReadInt32LittleEndian(data[0x2C..]);
        return sets > 0 && sets < 0x400 && setsOffset > 0 && setsOffset + sets * 4 <= data.Length;
    }

    private static bool IsTextureSet(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x30)
            return false;
        int size = BinaryPrimitives.ReadInt32LittleEndian(data[0x0C..]);
        int textures = BinaryPrimitives.ReadUInt16LittleEndian(data[0x14..]);
        int texturesOffset = BinaryPrimitives.ReadInt32LittleEndian(data[0x18..]);
        int transfersOffset = BinaryPrimitives.ReadInt32LittleEndian(data[0x1C..]);
        return size > 0x30 && size <= data.Length && textures > 0 && textures < 0x1000
            && texturesOffset >= 0x30 && transfersOffset >= 0x30
            && texturesOffset + textures * 0x28 <= size && transfersOffset < size;
    }
}
