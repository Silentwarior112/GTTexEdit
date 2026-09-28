namespace GTTexEdit.Core.Editing;

/// <summary>A picture for one view of a buffer, in one variation.</summary>
public sealed record ViewPicture(int View, int Variation, RgbaImage Image);

/// <summary>
/// Working out what a buffer's index image and palettes must be, given the pictures every view of it is to show.
///
/// There is no fitting or approximating here, deliberately. A buffer's texels are SHARED: one index value picks a
/// row of the joint table, and every view - in every variation - shows its own column of that row. So the number
/// of colours a set of pictures needs is the number of distinct COMBINATIONS across all of them, which can be far
/// more than any one picture uses. Rather than quietly squeeze that down, this counts them and says the number,
/// and the caller is expected to come back with content that fits. What it produces when they do fit is exact:
/// every picture comes out pixel for pixel as it was given.
/// </summary>
internal static class DepthChange
{
    /// <summary>What the pictures work out to, or how many colours they would need when that is too many.</summary>
    /// <param name="Indices">One index per texel of the buffer, or null when they do not fit.</param>
    /// <param name="Palettes">The palette each view shows in each variation, or null when they do not fit.</param>
    /// <param name="Combinations">How many distinct colour combinations the pictures ask for.</param>
    internal sealed record Solution(byte[]? Indices, Dictionary<(int View, int Variation), uint[]>? Palettes, int Combinations)
    {
        public bool Fits => Indices is not null;
    }

    /// <summary>
    /// Works out the index image and every palette from the pictures. <paramref name="paletteSize"/> is how many
    /// colours the buffer is to have - 16 or 256.
    /// </summary>
    public static Solution Solve(int width, int height, int paletteSize, IReadOnlyList<ViewPicture> pictures)
    {
        ArgumentNullException.ThrowIfNull(pictures);
        if (pictures.Count == 0)
            throw new ArgumentException("There is nothing to put in the buffer.", nameof(pictures));

        // a fixed order for the columns, so a combination means the same thing throughout
        (int View, int Variation)[] columns = [.. pictures.Select(p => (p.View, p.Variation)).Distinct()
            .OrderBy(c => c.Item1).ThenBy(c => c.Item2)];
        var byColumn = pictures.ToDictionary(p => (p.View, p.Variation), p => p.Image);

        foreach (RgbaImage image in byColumn.Values)
        {
            if (image.Width != width || image.Height != height)
                throw new ArgumentException($"Every picture has to be {width}x{height}; one is {image.Width}x{image.Height}.", nameof(pictures));
        }

        int texels = width * height;
        var indices = new byte[texels];
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<uint[]>();

        for (int texel = 0; texel < texels; texel++)
        {
            var combination = new uint[columns.Length];
            for (int c = 0; c < columns.Length; c++)
                combination[c] = byColumn[columns[c]].GetPixel(texel % width, texel / width);

            string key = string.Create(combination.Length * 2, combination, (span, source) =>
            {
                for (int i = 0; i < source.Length; i++)
                {
                    span[i * 2] = (char)(source[i] & 0xFFFF);
                    span[i * 2 + 1] = (char)(source[i] >> 16);
                }
            });

            if (!seen.TryGetValue(key, out int index))
            {
                index = rows.Count;
                seen[key] = index;
                rows.Add(combination);
                if (rows.Count > paletteSize)
                {
                    // Keep counting, so the caller can be told how far over it is rather than just "too many".
                    for (int rest = texel + 1; rest < texels; rest++)
                    {
                        var more = new uint[columns.Length];
                        for (int c = 0; c < columns.Length; c++)
                            more[c] = byColumn[columns[c]].GetPixel(rest % width, rest / width);
                        string other = string.Create(more.Length * 2, more, (span, source) =>
                        {
                            for (int i = 0; i < source.Length; i++)
                            {
                                span[i * 2] = (char)(source[i] & 0xFFFF);
                                span[i * 2 + 1] = (char)(source[i] >> 16);
                            }
                        });
                        if (seen.TryAdd(other, seen.Count))
                            rows.Add(more);
                    }
                    return new Solution(null, null, rows.Count);
                }
            }
            indices[texel] = (byte)index;
        }

        // each column's palette is that column of every row, with the unused rows left black
        var palettes = new Dictionary<(int, int), uint[]>();
        for (int c = 0; c < columns.Length; c++)
        {
            var palette = new uint[paletteSize];
            for (int row = 0; row < rows.Count; row++)
                palette[row] = rows[row][c];
            palettes[columns[c]] = palette;
        }
        return new Solution(indices, palettes, rows.Count);
    }
}
