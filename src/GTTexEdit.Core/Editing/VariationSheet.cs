namespace GTTexEdit.Core.Editing;

/// <summary>
/// Every palette of the whole file, in ONE variation, as a single PNG to edit in an image editor. GTPatEdit calls
/// this a paint sheet; here it covers both ways a car holds its variations - a GT4 colour patch and a GT3 clut patch
/// table - because either way a variation is a whole set of palettes, and this is all of them at once.
///
/// One band per distinct palette (views that read the same palette in every variation share a band), part by part,
/// set by set:
///
///   [id] [thumbnails of up to 4 views that use it, in this variation]   [swatches: 16 per row]
///
/// A swatch is a 24x24 cell: a 1-pixel frame (WHITE = the variations already give this entry colours of their own,
/// DARK = they do not yet; editing it makes them) around the colour, drawn OPAQUE (22x16), above a grey strip
/// (22x6) that is the entry's alpha, black = 0, white = 255. Alpha is kept out of the colour on purpose: plenty of
/// textures do not use alpha and keep real colours under alpha 0, where a PNG would hide them and an image editor
/// may destroy them. Import reads the middle of the colour and the middle of the strip, so the frame, the
/// thumbnails and the background are free to be scribbled on; a strip that is no longer grey (painted over with the
/// colour) leaves the alpha as it is.
///
/// The two small blocks at the left edge identify the band (part, texture set, view). Import finds bands by those
/// markers, not by position, so a sheet still imports after variations were removed or reordered, after bands were
/// deleted from the sheet, or when it was exported from another variation. Leave them alone.
/// </summary>
public static class VariationSheet
{
    private const int Margin = 8, MarkerWidth = 8, Thumb = 64, ThumbGap = 4, MaxThumbs = 4, Cell = 24, Columns = 16, BandGap = 8;
    private const int ThumbsLeft = Margin + MarkerWidth + Margin;
    private const int SwatchesLeft = ThumbsLeft + MaxThumbs * (Thumb + ThumbGap) + Margin;
    private const int SheetWidth = SwatchesLeft + Columns * Cell + Margin;

    private const uint Background = 0xFF303030, VariesFrame = 0xFFFFFFFF, SharedFrame = 0xFF101010, NotEditable = 0xFF303030;
    private const int ColorRows = 16;   // of the 22 inside the frame; the rest is the alpha strip
    private const byte MarkerTag = 0xB1;

    /// <summary>One palette of one set, and the views that read it.</summary>
    private sealed record Band(int Part, int SetIndex, TextureSet Set, List<TextureView> Users)
    {
        public TextureView First => Users[0];
        public int Rows => (First.PaletteSize + Columns - 1) / Columns;
        public int Height => Math.Max(Thumb, Rows * Cell);
    }

    private static List<Band> GetBands(TextureDocument document, bool variesOnly)
    {
        ArgumentNullException.ThrowIfNull(document);
        var bands = new List<Band>();

        for (int p = 0; p < document.Parts.Count; p++)
        {
            for (int s = 0; s < document.Parts[p].Sets.Count; s++)
            {
                TextureSet set = document.Parts[p].Sets[s];
                var byPalette = new Dictionary<string, Band>();
                foreach (TextureView view in set.Views)
                {
                    if (view.PaletteSize == 0 || view.PaletteSources.All(o => o < 0))
                        continue;

                    // Two views share a band only when they read the same palette in EVERY variation: a GT3 car
                    // repoints each view on its own, so views that agree in variation 0 need not agree later.
                    string key = view.PaletteSize + ":" + string.Join(",",
                        Enumerable.Range(0, set.VariationCount).Select(v => view.SourcesFor(v)[0]));
                    if (byPalette.TryGetValue(key, out Band? band))
                    {
                        band.Users.Add(view);
                        continue;
                    }
                    if (variesOnly && !Varies(set, view))
                        continue;
                    byPalette[key] = band = new Band(p, s, set, [view]);
                    bands.Add(band);
                }
            }
        }
        return bands;
    }

    private static bool Varies(TextureSet set, TextureView view) =>
        Enumerable.Range(0, view.PaletteSize).Any(entry => set.GetEntryCoverage(view, entry) != Coverage.None);

    /// <summary>How many palettes a sheet would hold.</summary>
    public static int CountPalettes(TextureDocument document, bool variesOnly) => GetBands(document, variesOnly).Count;

    /// <param name="variesOnly">Only the palettes the variations already differ over - a much shorter sheet.</param>
    public static RgbaImage Export(TextureDocument document, int variation, bool variesOnly)
    {
        List<Band> bands = GetBands(document, variesOnly);
        if (bands.Count == 0)
        {
            throw new InvalidOperationException(variesOnly
                ? "No palette differs between the variations, so there is nothing to export. Export all palettes instead."
                : "There are no palettes to export.");
        }

        var sheet = new RgbaImage(SheetWidth, Margin + bands.Sum(b => b.Height + BandGap));
        Fill(sheet, 0, 0, sheet.Width, sheet.Height, Background);

        int y = Margin;
        foreach (Band band in bands)
        {
            // Identity markers: (0xC0 | part, texture set, view hi) and (view lo, palette size / 16, tag).
            Fill(sheet, Margin, y, MarkerWidth, 8, Pack((byte)(0xC0 | (band.Part & 0x0F)), (byte)band.SetIndex, (byte)(band.First.Index >> 8)));
            Fill(sheet, Margin, y + 8, MarkerWidth, 8, Pack((byte)band.First.Index, (byte)(band.First.PaletteSize / 16), MarkerTag));

            for (int t = 0; t < Math.Min(MaxThumbs, band.Users.Count); t++)
                DrawThumbnail(sheet, band.Set.Render(band.Users[t], variation), ThumbsLeft + t * (Thumb + ThumbGap), y);

            for (int entry = 0; entry < band.First.PaletteSize; entry++)
            {
                int x = SwatchesLeft + entry % Columns * Cell, top = y + entry / Columns * Cell;
                if (!band.Set.IsPaletteEntryEditable(band.First, entry))
                {
                    Fill(sheet, x, top, Cell, Cell, NotEditable);
                    continue;
                }
                Fill(sheet, x, top, Cell, Cell, band.Set.GetEntryCoverage(band.First, entry) == Coverage.None ? SharedFrame : VariesFrame);
                uint color = band.Set.GetPaletteColor(band.First, entry, variation);
                byte alpha = (byte)(color >> 24);
                Fill(sheet, x + 1, top + 1, Cell - 2, ColorRows, color | 0xFF000000);
                Fill(sheet, x + 1, top + 1 + ColorRows, Cell - 2, Cell - 2 - ColorRows, Pack(alpha, alpha, alpha));
            }
            y += band.Height + BandGap;
        }
        return sheet;
    }

    /// <summary>
    /// Reads a sheet into variation <paramref name="variation"/>: every swatch whose colour differs from what that
    /// variation currently shows is written. Returns how many entries changed.
    /// </summary>
    public static int Import(TextureDocument document, int variation, RgbaImage sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        if (sheet.Width < SheetWidth)
            throw new InvalidDataException($"This is not a variation sheet: it is {sheet.Width} pixels wide, a sheet is {SheetWidth}.");

        // A GT3 variation usually reads palettes other variations read too - a copied one reads all of its
        // source's - so writing one of those words would write theirs. Nothing is done about that here: the first
        // colour that actually differs makes the variation take a copy of the palette it is in (see
        // TextureSet.SetPaletteColor), so a sheet that changes nothing costs nothing.
        var bands = GetBands(document, variesOnly: false).ToDictionary(b => (b.Part, b.SetIndex, b.First.Index));
        int found = 0, changed = 0;
        for (int y = 0; y + 16 <= sheet.Height; y++)
        {
            uint first = sheet.GetPixel(Margin + 3, y + 3), second = sheet.GetPixel(Margin + 3, y + 11);
            bool isMarker = (first & 0xF0) == 0xC0 && first >> 24 == 0xFF && (second >> 16 & 0xFF) == MarkerTag && second >> 24 == 0xFF;
            if (!isMarker || sheet.GetPixel(Margin + 3, y) != first || (y > 0 && sheet.GetPixel(Margin + 3, y - 1) == first))
                continue;   // not the top row of a marker

            int part = (int)(first & 0x0F), set = (int)(first >> 8 & 0xFF);
            int view = (int)((first >> 16 & 0xFF) << 8 | second & 0xFF);
            if (!bands.TryGetValue((part, set, view), out Band? band))
                throw new InvalidDataException($"The sheet has a palette this file does not have (part {part}, texture set {set}, view t{view}). Was it exported from another file?");
            if (y + band.Rows * Cell > sheet.Height)
                throw new InvalidDataException("The sheet is cut off in the middle of a palette.");

            found++;
            for (int entry = 0; entry < band.First.PaletteSize; entry++)
            {
                // The entry has to be stored as a word in THIS variation - which for a GT3 car is another palette.
                if (!band.Set.IsPaletteEntryEditable(band.First, entry) || band.First.SourcesFor(variation)[entry] < 0)
                    continue;
                int x = SwatchesLeft + entry % Columns * Cell + Cell / 2, top = y + entry / Columns * Cell;

                uint strip = sheet.GetPixel(x, top + 1 + ColorRows + (Cell - 2 - ColorRows) / 2);
                bool isGrey = (byte)strip == (byte)(strip >> 8) && (byte)strip == (byte)(strip >> 16);
                uint alpha = isGrey ? strip & 0xFF : band.Set.GetPaletteColor(band.First, entry, variation) >> 24;
                uint color = sheet.GetPixel(x, top + 1 + ColorRows / 2) & 0x00FFFFFF | alpha << 24;

                if (band.Set.SetPaletteColor(band.First, entry, color, variation))
                    changed++;
            }
            y += 15;
        }

        if (found == 0)
            throw new InvalidDataException("This is not a variation sheet: none of its palette markers were found (were they painted over?).");
        return changed;
    }

    private static uint Pack(byte r, byte g, byte b) => (uint)(r | g << 8 | b << 16) | 0xFF000000;

    private static void Fill(RgbaImage image, int x, int y, int width, int height, uint color)
    {
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(image.Pixels.AsSpan());
        for (int row = y; row < y + height; row++)
            pixels.Slice(row * image.Width + x, width).Fill(color);
    }

    // Nearest-neighbour fit into the thumbnail square, enlarging small textures by whole factors only.
    private static void DrawThumbnail(RgbaImage sheet, RgbaImage texture, int left, int top)
    {
        double scale = Math.Min((double)Thumb / texture.Width, (double)Thumb / texture.Height);
        if (scale > 1)
            scale = Math.Floor(scale);
        int width = Math.Max(1, (int)(texture.Width * scale)), height = Math.Max(1, (int)(texture.Height * scale));
        int offsetX = left + (Thumb - width) / 2, offsetY = top + (Thumb - height) / 2;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint pixel = texture.GetPixel(Math.Min(texture.Width - 1, (int)(x / scale)), Math.Min(texture.Height - 1, (int)(y / scale)));
                sheet.SetPixel(offsetX + x, offsetY + y, (byte)pixel, (byte)(pixel >> 8), (byte)(pixel >> 16), (byte)(pixel >> 24));
            }
        }
    }
}
