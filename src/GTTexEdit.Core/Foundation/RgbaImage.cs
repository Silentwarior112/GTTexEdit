namespace GTTexEdit.Core;

/// <summary>
/// The one in-memory image type every codec speaks: tightly packed 8-bit RGBA, row-major, top row first.
/// Byte order per pixel is [R, G, B, A]; there is no stride padding (row length == Width * 4).
/// </summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Width * Height * 4 bytes, [R,G,B,A] per pixel.</summary>
    public byte[] Pixels { get; }

    public RgbaImage(int width, int height)
        : this(width, height, new byte[checked(width * height * 4)])
    {
    }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length != checked(width * height * 4))
            throw new ArgumentException($"Pixel buffer is {pixels.Length} bytes, expected {width * height * 4} for {width}x{height} RGBA.", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>Pixel packed as 0xAABBGGRR (the little-endian uint32 view of [R,G,B,A]).</summary>
    public uint GetPixel(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (uint)(Pixels[i] | (Pixels[i + 1] << 8) | (Pixels[i + 2] << 16) | (Pixels[i + 3] << 24));
    }

    public void SetPixel(int x, int y, byte r, byte g, byte b, byte a)
    {
        int i = (y * Width + x) * 4;
        Pixels[i] = r;
        Pixels[i + 1] = g;
        Pixels[i + 2] = b;
        Pixels[i + 3] = a;
    }

    public RgbaImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

    /// <summary>Returns a vertically flipped copy.</summary>
    public RgbaImage FlipVertical()
    {
        var flipped = new byte[Pixels.Length];
        int row = Width * 4;
        for (int y = 0; y < Height; y++)
            Buffer.BlockCopy(Pixels, y * row, flipped, (Height - 1 - y) * row, row);
        return new RgbaImage(Width, Height, flipped);
    }

    /// <summary>
    /// Number of distinct RGBA values (alpha included). This is the count the Tex1 AUTO format pick
    /// uses (PSMT4 up to 16, PSMT8 up to 256) and what "Inspect color depths" reports.
    /// Stops early and returns <paramref name="stopAfter"/> + 1 once the count exceeds it.
    /// </summary>
    public int CountDistinctColors(int stopAfter = int.MaxValue)
    {
        var seen = new HashSet<uint>();
        var px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(Pixels);
        foreach (uint p in px)
        {
            if (seen.Add(p) && seen.Count > stopAfter)
                return seen.Count;
        }
        return seen.Count;
    }
}
