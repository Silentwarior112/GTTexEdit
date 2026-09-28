using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace GTTexEdit.Core.Imaging;

/// <summary>
/// Self-contained PNG codec (BCL only; zlib via System.IO.Compression.ZLibStream).
///
/// Decode: every PNG colour type / bit depth (grey 1/2/4/8/16, RGB 8/16, palette 1/2/4/8, grey+alpha
/// 8/16, RGBA 8/16), tRNS (palette alpha and grey/RGB colour keys), Adam7 interlace, all five filters,
/// multiple IDAT chunks. Colour-management chunks (gAMA / sRGB / iCCP / cHRM) are IGNORED on purpose:
/// texture pixels must pass through byte-exact, never gamma-converted. 16-bit samples narrow to 8-bit
/// with (v * 255 + 32895) >> 16.
///
/// Encode: always 8-bit RGBA (colour type 6), non-interlaced, no ancillary chunks.
///
/// Malformed input throws <see cref="InvalidDataException"/>. CRCs are verified on the critical chunks
/// (IHDR / PLTE / IDAT / IEND) only; ancillary chunks - tRNS included - are never CRC-checked, so a sloppy
/// metadata writer cannot make an otherwise good image unloadable.
/// </summary>
public static class Png
{
    // Chunk types as big-endian FourCCs.
    private const uint IHDR = 0x49484452;
    private const uint PLTE = 0x504C5445;
    private const uint IDAT = 0x49444154;
    private const uint IEND = 0x49454E44;
    private const uint TRNS = 0x74524E53;

    private const int Grey = 0, Rgb = 2, Indexed = 3, GreyAlpha = 4, RgbAlpha = 6;

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Adam7: origin and spacing of each of the seven passes.
    private static ReadOnlySpan<byte> PassXStart => [0, 4, 0, 2, 0, 1, 0];
    private static ReadOnlySpan<byte> PassYStart => [0, 0, 4, 0, 2, 0, 1];
    private static ReadOnlySpan<byte> PassXStep => [8, 8, 4, 4, 2, 2, 1];
    private static ReadOnlySpan<byte> PassYStep => [8, 8, 8, 4, 4, 2, 2];

    private static readonly uint[] CrcTable = BuildCrcTable();

    public static bool IsPng(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    public static RgbaImage Load(string path) => Decode(File.ReadAllBytes(path));

    public static void Save(RgbaImage image, string path) => File.WriteAllBytes(path, Encode(image));

    // ------------------------------------------------------------------------------------------------
    // Decode
    // ------------------------------------------------------------------------------------------------

    private readonly record struct Header(int Width, int Height, int BitDepth, int ColorType, bool Interlaced)
    {
        public int BitsPerPixel => BitDepth * ColorType switch { Grey or Indexed => 1, GreyAlpha => 2, Rgb => 3, _ => 4 };

        /// <summary>Byte distance to the "left" pixel for filtering; 1 for the sub-byte depths.</summary>
        public int FilterStride => Math.Max(1, BitsPerPixel / 8);

        public long RowBytes(int pixels) => ((long)pixels * BitsPerPixel + 7) / 8;
    }

    public static RgbaImage Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsPng(data))
            throw new InvalidDataException("Not a PNG file (bad signature).");

        Header? parsedHeader = null;
        ReadOnlySpan<byte> palette = default, transparency = default;
        var idat = new List<(int Offset, int Length)>();
        long idatTotal = 0;

        for (int pos = Signature.Length; pos < data.Length;)
        {
            if (data.Length - pos < 12)
                throw new InvalidDataException($"PNG is truncated: incomplete chunk header at offset {pos}.");

            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            uint type = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 4));
            if (length > (uint)(data.Length - pos - 12))
                throw new InvalidDataException($"PNG is truncated: chunk '{ChunkName(type)}' at offset {pos} declares {length} bytes, more than the file holds.");

            int start = pos + 8, size = (int)length;
            bool critical = (type & 0x20000000) == 0; // ancillary chunks have a lowercase first letter
            if (critical && Crc32(data.AsSpan(pos + 4, size + 4)) != BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start + size)))
                throw new InvalidDataException($"PNG is corrupt: CRC mismatch in chunk '{ChunkName(type)}' at offset {pos}.");

            // A critical chunk we do not know means the pixels cannot be interpreted safely. This is also what
            // rejects Apple's "CgBI" files, which look like PNG but hold raw-deflate premultiplied BGRA.
            if (critical && type is not (IHDR or PLTE or IDAT or IEND))
                throw new InvalidDataException($"PNG uses unsupported critical chunk '{ChunkName(type)}'.");
            if ((parsedHeader is null) != (type == IHDR))
                throw new InvalidDataException(parsedHeader is null ? "PNG is malformed: the first chunk is not IHDR." : "PNG is malformed: more than one IHDR chunk.");

            ReadOnlySpan<byte> body = data.AsSpan(start, size);
            switch (type)
            {
                case IHDR:
                    parsedHeader = ParseHeader(body);
                    break;
                case PLTE:
                    if (!palette.IsEmpty || idat.Count > 0)
                        throw new InvalidDataException("PNG is malformed: misplaced or duplicate PLTE chunk.");
                    if (size == 0 || size % 3 != 0 || size > 256 * 3)
                        throw new InvalidDataException($"PNG is malformed: PLTE chunk has invalid length {size}.");
                    palette = body;
                    break;
                case IDAT:
                    idat.Add((start, size));
                    idatTotal += size;
                    break;
                case TRNS:
                    transparency = body;
                    break;
            }

            if (type == IEND)
                break;
            pos = start + size + 4;
        }

        Header header = parsedHeader ?? throw new InvalidDataException("PNG is truncated: no IHDR chunk.");
        if (idat.Count == 0)
            throw new InvalidDataException("PNG is malformed: no IDAT chunk (no image data).");
        PixelConverter converter = new(header, palette, transparency);

        long pixelCount = (long)header.Width * header.Height;
        long rawSize = pixelCount <= Array.MaxLength / 4 ? RawSize(header) : long.MaxValue;
        if (rawSize > Array.MaxLength)
            throw new InvalidDataException($"PNG is too large to load ({header.Width}x{header.Height}, {header.BitsPerPixel} bits per pixel).");

        // Deflate cannot expand by more than 1032:1, so a header this much bigger than the data is corrupt;
        // failing here avoids allocating gigabytes on the say-so of a damaged IHDR.
        if (rawSize > idatTotal * 1032 + 1024)
            throw new InvalidDataException($"PNG image data is truncated: {idatTotal} compressed bytes cannot hold a {header.Width}x{header.Height} image.");

        byte[] raw = Inflate(data, idat, (int)idatTotal, (int)rawSize);
        byte[] pixels = GC.AllocateUninitializedArray<byte>((int)pixelCount * 4);

        if (!header.Interlaced)
        {
            int rowBytes = (int)header.RowBytes(header.Width);
            int outStride = header.Width * 4;
            Unfilter(raw, rowBytes, header.Height, header.FilterStride);
            for (int y = 0; y < header.Height; y++)
                converter.Convert(raw.AsSpan(y * (rowBytes + 1) + 1, rowBytes), pixels.AsSpan(y * outStride, outStride));
        }
        else
        {
            DecodeAdam7(header, raw, converter, pixels);
        }

        return new RgbaImage(header.Width, header.Height, pixels);
    }

    private static Header ParseHeader(ReadOnlySpan<byte> body)
    {
        if (body.Length != 13)
            throw new InvalidDataException($"PNG is malformed: IHDR chunk is {body.Length} bytes, expected 13.");

        uint width = BinaryPrimitives.ReadUInt32BigEndian(body);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
        int bitDepth = body[8], colorType = body[9];
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
            throw new InvalidDataException($"PNG has invalid dimensions {width}x{height}.");

        bool validFormat = colorType switch
        {
            Grey => bitDepth is 1 or 2 or 4 or 8 or 16,
            Indexed => bitDepth is 1 or 2 or 4 or 8,
            Rgb or GreyAlpha or RgbAlpha => bitDepth is 8 or 16,
            _ => false,
        };
        if (!validFormat)
            throw new InvalidDataException($"PNG has an invalid colour type / bit depth combination ({colorType} / {bitDepth}).");
        if (body[10] != 0 || body[11] != 0)
            throw new InvalidDataException($"PNG uses unknown compression method {body[10]} / filter method {body[11]}.");
        if (body[12] > 1)
            throw new InvalidDataException($"PNG uses unknown interlace method {body[12]}.");

        return new Header((int)width, (int)height, bitDepth, colorType, body[12] == 1);
    }

    /// <summary>Size of the inflated IDAT stream: every scanline plus its leading filter-type byte.</summary>
    private static long RawSize(Header header)
    {
        if (!header.Interlaced)
            return (header.RowBytes(header.Width) + 1) * header.Height;

        long total = 0;
        for (int pass = 0; pass < 7; pass++)
        {
            var (w, h) = PassSize(header, pass);
            if (w > 0 && h > 0)
                total += (header.RowBytes(w) + 1) * h;
        }
        return total;
    }

    private static (int Width, int Height) PassSize(Header header, int pass) =>
        ((header.Width - PassXStart[pass] + PassXStep[pass] - 1) / PassXStep[pass],
         (header.Height - PassYStart[pass] + PassYStep[pass] - 1) / PassYStep[pass]);

    private static byte[] Inflate(byte[] data, List<(int Offset, int Length)> idat, int idatTotal, int rawSize)
    {
        // The zlib stream runs across all IDAT chunks; only a split stream needs stitching together.
        MemoryStream compressed;
        if (idat.Count == 1)
        {
            compressed = new MemoryStream(data, idat[0].Offset, idat[0].Length, writable: false);
        }
        else
        {
            byte[] joined = new byte[idatTotal];
            int at = 0;
            foreach (var (offset, length) in idat)
            {
                data.AsSpan(offset, length).CopyTo(joined.AsSpan(at));
                at += length;
            }
            compressed = new MemoryStream(joined, writable: false);
        }

        byte[] raw = GC.AllocateUninitializedArray<byte>(rawSize);
        int read;
        try
        {
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            read = zlib.ReadAtLeast(raw, rawSize, throwOnEndOfStream: false);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException("PNG image data is corrupt (zlib stream cannot be decompressed).", e);
        }

        if (read < rawSize)
            throw new InvalidDataException($"PNG image data is truncated: decompressed {read} of {rawSize} bytes.");
        return raw;
    }

    private static void DecodeAdam7(Header header, byte[] raw, PixelConverter converter, byte[] pixels)
    {
        Span<uint> output = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        byte[] passRow = new byte[header.Width * 4];
        int offset = 0;

        for (int pass = 0; pass < 7; pass++)
        {
            var (passWidth, passHeight) = PassSize(header, pass);
            if (passWidth <= 0 || passHeight <= 0)
                continue; // an empty pass has no scanlines at all, not even filter bytes

            int rowBytes = (int)header.RowBytes(passWidth);
            int passBytes = (rowBytes + 1) * passHeight;
            Unfilter(raw.AsSpan(offset, passBytes), rowBytes, passHeight, header.FilterStride);

            Span<byte> rgba = passRow.AsSpan(0, passWidth * 4);
            ReadOnlySpan<uint> rgbaPixels = MemoryMarshal.Cast<byte, uint>(rgba);
            int xStart = PassXStart[pass], xStep = PassXStep[pass];
            for (int row = 0; row < passHeight; row++)
            {
                converter.Convert(raw.AsSpan(offset + row * (rowBytes + 1) + 1, rowBytes), rgba);
                Span<uint> target = output.Slice((PassYStart[pass] + row * PassYStep[pass]) * header.Width, header.Width);
                for (int i = 0, x = xStart; i < rgbaPixels.Length; i++, x += xStep)
                    target[x] = rgbaPixels[i];
            }

            offset += passBytes;
        }
    }

    /// <summary>
    /// Reverses the scanline filters in place. <paramref name="raw"/> holds <paramref name="rows"/> scanlines of
    /// one filter-type byte plus <paramref name="rowBytes"/> data bytes. The row above the first one and the
    /// pixel left of the first one count as zero.
    /// </summary>
    private static unsafe void Unfilter(Span<byte> raw, int rowBytes, int rows, int stride)
    {
        fixed (byte* start = raw)
        {
            byte* prev = null;
            for (int y = 0; y < rows; y++)
            {
                byte* cur = start + (nint)y * (rowBytes + 1) + 1;
                int filter = cur[-1];

                // On the first row "up" is all zeros: Up becomes None, Paeth becomes Sub, Average halves the left.
                // The first pixel of a row (rowBytes >= stride always) has no left / upper-left neighbour.
                switch (filter)
                {
                    case 0:
                        break;

                    case 1:
                    case 4 when prev == null:
                        for (int i = stride; i < rowBytes; i++)
                            cur[i] += cur[i - stride];
                        break;

                    case 2:
                        if (prev != null)
                        {
                            for (int i = 0; i < rowBytes; i++)
                                cur[i] += prev[i];
                        }
                        break;

                    case 3:
                        if (prev == null)
                        {
                            for (int i = stride; i < rowBytes; i++)
                                cur[i] += (byte)(cur[i - stride] >> 1);
                        }
                        else
                        {
                            for (int i = 0; i < stride; i++)
                                cur[i] += (byte)(prev[i] >> 1);
                            for (int i = stride; i < rowBytes; i++)
                                cur[i] += (byte)((cur[i - stride] + prev[i]) >> 1);
                        }
                        break;

                    case 4:
                        for (int i = 0; i < stride; i++)
                            cur[i] += prev[i];
                        for (int i = stride; i < rowBytes; i++)
                            cur[i] += (byte)Paeth(cur[i - stride], prev[i], prev[i - stride]);
                        break;

                    default:
                        throw new InvalidDataException($"PNG is corrupt: scanline {y} has unknown filter type {filter}.");
                }

                prev = cur;
            }
        }
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        int pa = up - upLeft, pb = left - upLeft;
        int pc = Math.Abs(pa + pb);
        pa = Math.Abs(pa);
        pb = Math.Abs(pb);
        return pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
    }

    /// <summary>Expands one unfiltered scanline of any PNG pixel format to 8-bit RGBA.</summary>
    private readonly ref struct PixelConverter
    {
        private readonly int _colorType, _bitDepth;
        private readonly bool _hasKey;
        private readonly int _keyR, _keyG, _keyB;

        // Greyscale up to 8 bits and palette images share one path: sample -> RGBA lookup. Stored as bytes and
        // read back as uint so the [R,G,B,A] byte order holds on any endianness.
        private readonly ReadOnlySpan<uint> _lut;

        public PixelConverter(Header header, ReadOnlySpan<byte> palette, ReadOnlySpan<byte> transparency)
        {
            _colorType = header.ColorType;
            _bitDepth = header.BitDepth;

            if (_colorType == Indexed)
            {
                if (palette.IsEmpty)
                    throw new InvalidDataException("PNG is malformed: palette image without a PLTE chunk.");
                if (transparency.Length > 256)
                    throw new InvalidDataException($"PNG is malformed: tRNS chunk has invalid length {transparency.Length}.");

                // Indices past the end of the palette come out opaque black, as in libpng and every viewer.
                byte[] lut = new byte[256 * 4];
                for (int i = 0; i < 256; i++)
                    lut[i * 4 + 3] = 255;
                for (int i = 0; i < palette.Length / 3; i++)
                    palette.Slice(i * 3, 3).CopyTo(lut.AsSpan(i * 4));
                for (int i = 0; i < transparency.Length; i++)
                    lut[i * 4 + 3] = transparency[i];
                _lut = MemoryMarshal.Cast<byte, uint>(lut);
                return;
            }

            // Colour keys are compared at the file's own bit depth; below 16 bits only the low bits count.
            if (_colorType is Grey or Rgb && !transparency.IsEmpty)
            {
                int channels = _colorType == Grey ? 1 : 3;
                if (transparency.Length != channels * 2)
                    throw new InvalidDataException($"PNG is malformed: tRNS chunk has invalid length {transparency.Length}.");

                int mask = (1 << _bitDepth) - 1;
                _hasKey = true;
                _keyR = BinaryPrimitives.ReadUInt16BigEndian(transparency) & mask;
                if (channels == 3)
                {
                    _keyG = BinaryPrimitives.ReadUInt16BigEndian(transparency[2..]) & mask;
                    _keyB = BinaryPrimitives.ReadUInt16BigEndian(transparency[4..]) & mask;
                }
            }

            if (_colorType == Grey && _bitDepth <= 8)
            {
                // Bit replication: 1-bit 0/255, 2-bit x85, 4-bit x17.
                int levels = 1 << _bitDepth, scale = 255 / (levels - 1);
                byte[] lut = new byte[256 * 4];
                for (int i = 0; i < levels; i++)
                {
                    lut.AsSpan(i * 4, 3).Fill((byte)(i * scale));
                    lut[i * 4 + 3] = _hasKey && i == _keyR ? (byte)0 : (byte)255;
                }
                _lut = MemoryMarshal.Cast<byte, uint>(lut);
            }
        }

        /// <param name="src">One unfiltered scanline (without the filter byte).</param>
        /// <param name="dst">Exactly 4 bytes per pixel of that scanline.</param>
        public void Convert(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            if (!_lut.IsEmpty)
                ConvertLookup(src, MemoryMarshal.Cast<byte, uint>(dst));
            else if (_bitDepth == 8)
                Convert8(src, dst);
            else
                Convert16(src, dst);
        }

        private void ConvertLookup(ReadOnlySpan<byte> src, Span<uint> dst)
        {
            ReadOnlySpan<uint> lut = _lut;
            if (_bitDepth == 8)
            {
                src = src[..dst.Length];
                for (int i = 0; i < src.Length; i++)
                    dst[i] = lut[src[i]];
                return;
            }

            // Packed samples, leftmost pixel in the high bits.
            int depth = _bitDepth, mask = (1 << depth) - 1;
            int x = 0;
            for (int s = 0; x < dst.Length; s++)
            {
                int packed = src[s];
                for (int shift = 8 - depth; shift >= 0 && x < dst.Length; shift -= depth)
                    dst[x++] = lut[(packed >> shift) & mask];
            }
        }

        private void Convert8(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int count = dst.Length / 4;
            switch (_colorType)
            {
                case RgbAlpha:
                    src[..dst.Length].CopyTo(dst);
                    break;

                case Rgb:
                    src = src[..(count * 3)];
                    for (int s = 0, d = 0; s < src.Length; s += 3, d += 4)
                    {
                        byte r = src[s], g = src[s + 1], b = src[s + 2];
                        dst[d] = r;
                        dst[d + 1] = g;
                        dst[d + 2] = b;
                        dst[d + 3] = _hasKey && r == _keyR && g == _keyG && b == _keyB ? (byte)0 : (byte)255;
                    }
                    break;

                default: // GreyAlpha
                    src = src[..(count * 2)];
                    for (int s = 0, d = 0; s < src.Length; s += 2, d += 4)
                    {
                        byte v = src[s];
                        dst[d] = v;
                        dst[d + 1] = v;
                        dst[d + 2] = v;
                        dst[d + 3] = src[s + 1];
                    }
                    break;
            }
        }

        private void Convert16(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int count = dst.Length / 4;
            switch (_colorType)
            {
                case Grey:
                    for (int i = 0, d = 0; i < count; i++, d += 4)
                    {
                        int v = Sample16(src, i * 2);
                        dst.Slice(d, 3).Fill(Narrow(v));
                        dst[d + 3] = _hasKey && v == _keyR ? (byte)0 : (byte)255;
                    }
                    break;

                case GreyAlpha:
                    for (int i = 0, d = 0; i < count; i++, d += 4)
                    {
                        dst.Slice(d, 3).Fill(Narrow(Sample16(src, i * 4)));
                        dst[d + 3] = Narrow(Sample16(src, i * 4 + 2));
                    }
                    break;

                case Rgb:
                    for (int i = 0, d = 0; i < count; i++, d += 4)
                    {
                        int r = Sample16(src, i * 6), g = Sample16(src, i * 6 + 2), b = Sample16(src, i * 6 + 4);
                        dst[d] = Narrow(r);
                        dst[d + 1] = Narrow(g);
                        dst[d + 2] = Narrow(b);
                        dst[d + 3] = _hasKey && r == _keyR && g == _keyG && b == _keyB ? (byte)0 : (byte)255;
                    }
                    break;

                default: // RgbAlpha
                    for (int i = 0; i < dst.Length; i++)
                        dst[i] = Narrow(Sample16(src, i * 2));
                    break;
            }
        }

        private static int Sample16(ReadOnlySpan<byte> src, int offset) => (src[offset] << 8) | src[offset + 1];

        private static byte Narrow(int v) => (byte)((v * 255 + 32895) >> 16);
    }

    // ------------------------------------------------------------------------------------------------
    // Encode
    // ------------------------------------------------------------------------------------------------

    public static byte[] Encode(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int stride = image.Width * 4;

        var output = new MemoryStream(1024 + image.Pixels.Length / 4);
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)image.Height);
        header[8] = 8;          // bit depth
        header[9] = RgbAlpha;   // colour type; compression, filter and interlace methods stay 0
        WriteChunk(output, IHDR, header);

        // IDAT is compressed straight into the output; its length and CRC are patched in afterwards.
        int idatStart = (int)output.Position;
        Span<byte> chunkHead = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(chunkHead[4..], IDAT);
        output.Write(chunkHead);

        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            // Per scanline, try all five filters and keep the one with the smallest sum of absolute
            // (signed) residuals - the usual libpng heuristic. Ties go to the lower filter number, and a
            // sum of zero (e.g. a fully transparent row) cannot be beaten, so the search stops there.
            byte[] best = new byte[stride + 1], candidate = new byte[stride + 1];
            ReadOnlySpan<byte> prev = new byte[stride];
            for (int y = 0; y < image.Height; y++)
            {
                ReadOnlySpan<byte> cur = image.Pixels.AsSpan(y * stride, stride);
                long bestSum = Filter(0, cur, prev, best);
                for (int filter = 1; filter <= 4 && bestSum > 0; filter++)
                {
                    long sum = Filter(filter, cur, prev, candidate);
                    if (sum < bestSum)
                    {
                        bestSum = sum;
                        (best, candidate) = (candidate, best);
                    }
                }

                zlib.Write(best);
                prev = cur;
            }
        }

        int idatLength = checked((int)output.Position - idatStart - 8);
        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(output.GetBuffer().AsSpan(idatStart), (uint)idatLength);
        BinaryPrimitives.WriteUInt32BigEndian(trailer, Crc32(output.GetBuffer().AsSpan(idatStart + 4, idatLength + 4)));
        output.Write(trailer);

        WriteChunk(output, IEND, default);
        return output.ToArray();
    }

    /// <summary>
    /// Writes the filter-type byte and the filtered RGBA scanline into <paramref name="dest"/>; returns the sum
    /// of the residuals' magnitudes (each byte read as signed).
    /// </summary>
    private static unsafe long Filter(int filter, ReadOnlySpan<byte> cur, ReadOnlySpan<byte> prev, Span<byte> dest)
    {
        const int Bpp = 4;
        int n = cur.Length;
        dest[0] = (byte)filter;

        fixed (byte* c = cur, p = prev, d = dest[1..])
        {
            long sum = 0;
            switch (filter)
            {
                case 0:
                    for (int i = 0; i < n; i++)
                        sum += Magnitude(d[i] = c[i]);
                    break;
                case 1:
                    for (int i = 0; i < Bpp; i++)
                        sum += Magnitude(d[i] = c[i]);
                    for (int i = Bpp; i < n; i++)
                        sum += Magnitude(d[i] = (byte)(c[i] - c[i - Bpp]));
                    break;
                case 2:
                    for (int i = 0; i < n; i++)
                        sum += Magnitude(d[i] = (byte)(c[i] - p[i]));
                    break;
                case 3:
                    for (int i = 0; i < Bpp; i++)
                        sum += Magnitude(d[i] = (byte)(c[i] - (p[i] >> 1)));
                    for (int i = Bpp; i < n; i++)
                        sum += Magnitude(d[i] = (byte)(c[i] - ((c[i - Bpp] + p[i]) >> 1)));
                    break;
                default:
                    for (int i = 0; i < Bpp; i++)
                        sum += Magnitude(d[i] = (byte)(c[i] - p[i]));
                    for (int i = Bpp; i < n; i++)
                        sum += Magnitude(d[i] = (byte)(c[i] - Paeth(c[i - Bpp], p[i], p[i - Bpp])));
                    break;
            }
            return sum;
        }
    }

    private static int Magnitude(byte residual) => Math.Abs((int)(sbyte)residual);

    private static void WriteChunk(MemoryStream output, uint type, ReadOnlySpan<byte> body)
    {
        Span<byte> chunk = stackalloc byte[8 + 13 + 4]; // only ever IHDR (13 bytes) and IEND (0 bytes)
        chunk = chunk[..(8 + body.Length + 4)];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)body.Length);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[4..], type);
        body.CopyTo(chunk[8..]);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[(8 + body.Length)..], Crc32(chunk.Slice(4, 4 + body.Length)));
        output.Write(chunk);
    }

    // ------------------------------------------------------------------------------------------------
    // CRC-32 (System.IO.Hashing is not part of the BCL)
    // ------------------------------------------------------------------------------------------------

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint[] table = CrcTable;
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = table[(byte)(crc ^ b)] ^ (crc >> 8);
        return ~crc;
    }

    private static string ChunkName(uint type)
    {
        Span<char> name = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            char ch = (char)(byte)(type >> (24 - i * 8));
            name[i] = char.IsAsciiLetter(ch) ? ch : '?';
        }
        return new string(name);
    }
}
