using System.Buffers.Binary;

namespace GTTexEdit.Core.Cars;

public enum Car4Section
{
    CarInfo, Collision, MainModel, MainColorPatch, WheelModel, WheelColorPatch, WingModel, TireModel0, TireModel1, DriverModel, TextureSet,
}

/// <summary>
/// GT4 car container ("CAR4"): a 0x40-byte header of absolute section offsets (0 = absent) followed by the
/// sections in header order. Two layouts exist:
///   menu cars  - both colour patches embedded (0x1C main, 0x24 wheel);
///   race cars  - the main patch is NOT in the file (0x1C = 0) but ships beside it as "&lt;name&gt;.pat".
/// Sections are kept as raw bytes including the zero padding up to the next section, so a car is written back
/// byte for byte; only a colour patch that changed is re-packed. Packing facts (measured on 1,613 cars): models
/// sit on a 0x40 grid, nothing pads the end of the file, and every patch is followed by a model.
/// </summary>
public sealed class Car4File
{
    private const uint Magic = 0x34524143; // "CAR4"
    private const int HeaderSize = 0x40;
    private const int SectionCount = 11;
    private const int ModelAlignment = 0x40;

    private readonly byte[] _header;
    private readonly byte[]?[] _sections = new byte[]?[SectionCount];

    private Car4File(byte[] header) => _header = header;

    /// <summary>The external main colour patch of a race car (null for menu cars, which embed it).</summary>
    public byte[]? ExternalMainPatch { get; private set; }

    public bool HasExternalMainPatch => ExternalMainPatch is not null;

    /// <summary>Path of the external patch belonging to a car file.</summary>
    public static string ExternalPatchPath(string carPath) => carPath + ".pat";

    public static bool IsCar4(ReadOnlySpan<byte> data) => data.Length >= HeaderSize && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    public static Car4File Load(string path)
    {
        Car4File car = Read(File.ReadAllBytes(path));
        if (car[Car4Section.MainColorPatch] is null && File.Exists(ExternalPatchPath(path)))
            car.ExternalMainPatch = File.ReadAllBytes(ExternalPatchPath(path));
        return car;
    }

    public static Car4File Read(byte[] data)
    {
        if (!IsCar4(data))
            throw new InvalidDataException("Not a GT4 car model (the \"CAR4\" magic is missing).");

        var car = new Car4File(data.AsSpan(0, HeaderSize).ToArray());
        var offsets = new int[SectionCount];
        for (int i = 0; i < SectionCount; i++)
            offsets[i] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x10 + i * 4));

        for (int i = 0; i < SectionCount; i++)
        {
            if (offsets[i] == 0)
                continue;
            int end = offsets.Where(o => o > offsets[i]).DefaultIfEmpty(data.Length).Min();
            if (offsets[i] < HeaderSize || end > data.Length)
                throw new InvalidDataException($"Corrupt car model: section {(Car4Section)i} lies outside the file.");
            car._sections[i] = data.AsSpan(offsets[i], end - offsets[i]).ToArray();
        }
        return car;
    }

    /// <summary>A section's bytes, including any zero padding before the next section. Null when absent.</summary>
    public byte[]? this[Car4Section section] => _sections[(int)section];

    /// <summary>The main colour patch wherever it lives: embedded (menu car) or external (race car).</summary>
    public byte[]? MainColorPatch => this[Car4Section.MainColorPatch] ?? ExternalMainPatch;

    /// <summary>
    /// Replaces a model. It may be longer than the one it replaces: a colour patch addresses the model from the
    /// model's own start, so nothing it points at moves as long as the new bytes only ADD to the end - and where
    /// the model sits in the car is the car's own business, which <see cref="Write"/> works out again every time.
    /// </summary>
    public void ReplaceModel(Car4Section section, byte[] model)
    {
        ArgumentNullException.ThrowIfNull(model);
        byte[] current = _sections[(int)section] ?? throw new InvalidOperationException($"The car has no {section}.");
        if (model.Length <= current.Length)
        {
            model.CopyTo(current.AsSpan());
            return;
        }

        // The section carries the padding up to whatever follows it, so a longer one brings its own.
        var grown = new byte[(model.Length + ModelAlignment - 1) & ~(ModelAlignment - 1)];
        model.CopyTo(grown, 0);
        _sections[(int)section] = grown;
    }

    /// <summary>Replaces a colour patch. It may grow or shrink; <see cref="Write"/> re-packs the car around it.</summary>
    public void ReplaceColorPatch(Car4Section section, byte[] patch)
    {
        if (section == Car4Section.MainColorPatch && _sections[(int)section] is null)
            ExternalMainPatch = patch;
        else
            _sections[(int)section] = patch;
    }

    /// <summary>
    /// The car file. Every section but the patches goes back verbatim, padding included. A patch is written at its
    /// strict size and then zero-padded up to the model grid, because a model always follows it - which is exactly
    /// how the original files are packed, so an unedited car comes out byte for byte.
    /// </summary>
    public byte[] Write()
    {
        var w = new ByteWriter(capacity: HeaderSize + _sections.Sum(s => s?.Length ?? 0) + ModelAlignment * 2);
        w.WriteBytes(_header);

        for (int i = 0; i < SectionCount; i++)
        {
            byte[]? section = _sections[i];
            int start = section is null ? 0 : w.Length;
            w.Position = 0x10 + i * 4;
            w.WriteInt32(start);
            w.SeekEnd();
            if (section is null)
                continue;

            if ((Car4Section)i is Car4Section.MainColorPatch or Car4Section.WheelColorPatch)
            {
                w.WriteBytes(Pat0.Read(section).Write()); // drops whatever padding the section carried
                w.Align(ModelAlignment);
            }
            else
            {
                w.WriteBytes(section);
            }
        }

        int fileSize = w.Length;
        w.Position = 0x08;
        w.WriteInt32(fileSize);
        return w.ToArray();
    }
}
