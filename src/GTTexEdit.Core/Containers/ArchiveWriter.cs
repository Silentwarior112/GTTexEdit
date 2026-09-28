using System.Buffers.Binary;

namespace GTTexEdit.Core.Containers;

/// <summary>
/// Writes an offset-table archive back out with some of its constituents replaced - including by longer ones.
///
/// These archives (GT3 cars, GT3 and GT4 courses) are nothing but a table of absolute offsets followed by the files
/// it points at, each running to the next offset that is used. So a constituent has no length of its own: it is
/// simply everything up to the next one, padding included. That is what makes growing one possible at all - move
/// everything after it along and write the new offsets into the table - and it is also what makes it delicate,
/// because the table is the only description of where anything is.
///
/// Rewriting an archive that nothing has changed gives back the original bytes exactly, padding and all.
/// </summary>
internal static class ArchiveWriter
{
    /// <summary>Every constituent must start on this, because that is where the originals start.</summary>
    private const int Alignment = 0x40;

    /// <summary>One thing in the archive: the slots pointing at it, where it starts, and how far it runs.</summary>
    internal sealed record Region(List<int> Slots, int Offset, int Length);

    /// <summary>
    /// The archive's constituents in file order. Several slots may point at the same one; a slot of 0 is empty and
    /// points at nothing.
    /// </summary>
    internal static List<Region> Regions(byte[] file, int headerSize)
    {
        var byOffset = new Dictionary<int, List<int>>();
        for (int slot = 0; slot * 4 < headerSize; slot++)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(slot * 4));
            if (offset <= 0 || offset > file.Length)
                continue;
            if (!byOffset.TryGetValue(offset, out List<int>? slots))
                byOffset[offset] = slots = [];
            slots.Add(slot);
        }

        int[] starts = [.. byOffset.Keys.Order()];
        var regions = new List<Region>();
        for (int i = 0; i < starts.Length; i++)
        {
            int end = i + 1 < starts.Length ? starts[i + 1] : file.Length;
            regions.Add(new Region(byOffset[starts[i]], starts[i], end - starts[i]));
        }
        return regions;
    }

    /// <summary>A run of bytes that was read out of the file at <paramref name="Offset"/> and is going back in.</summary>
    /// <param name="Length">How long it was when it was read - it may be going back in longer.</param>
    internal sealed record Edit(int Offset, int Length, byte[] Bytes);

    /// <summary>
    /// The archive with every edit written back where it came from, every later constituent moved along to make
    /// room for any that grew, and the offset table rewritten to match.
    ///
    /// An edit that changes length has to BE a whole constituent. Some things the editor opens were found by
    /// searching inside a slot rather than through the table - the models packed into a GT3 wheel file, say - and
    /// there is nothing that says where those are, so growing one would shift its neighbours with nothing to
    /// follow them. Those may be written back, but only at the length they came out at.
    /// </summary>
    public static byte[] Write(byte[] file, IReadOnlyList<Edit> edits)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(edits);
        if (!ContentScanner.LooksLikeArchive(file, out int headerSize))
            throw new InvalidOperationException("This file is not an offset-table archive, so there is no table to rewrite.");

        List<Region> regions = Regions(file, headerSize);
        foreach (Edit edit in edits)
        {
            Region region = regions.Find(r => edit.Offset >= r.Offset && edit.Offset + edit.Length <= r.Offset + r.Length)
                ?? throw new InvalidOperationException($"Nothing in the archive covers {edit.Offset:X}..{edit.Offset + edit.Length:X}.");
            if (edit.Bytes.Length != edit.Length && edit.Offset != region.Offset)
            {
                throw new InvalidOperationException(
                    $"What sits at {edit.Offset:X} is inside slot {region.Slots[0]}, not the whole of it, and only the archive's own "
                    + "table says where anything is - so this cannot be made longer without losing whatever follows it in that slot.");
            }
        }

        // lay the constituents out again, in the order they were in
        var places = new List<(Region Region, byte[] Content, int At)>();
        int position = headerSize;
        foreach (Region region in regions)
        {
            byte[] content = file.AsSpan(region.Offset, region.Length).ToArray();
            foreach (Edit edit in edits.Where(e => e.Offset >= region.Offset && e.Offset < region.Offset + region.Length)
                                       .OrderByDescending(e => e.Offset))
            {
                int at = edit.Offset - region.Offset;
                var spliced = new byte[content.Length - edit.Length + edit.Bytes.Length];
                content.AsSpan(0, at).CopyTo(spliced);
                edit.Bytes.CopyTo(spliced.AsSpan(at));
                content.AsSpan(at + edit.Length).CopyTo(spliced.AsSpan(at + edit.Bytes.Length));
                content = spliced;
            }

            // A constituent that still fits keeps the room it had, so the padding after it is preserved byte for
            // byte and an archive nothing changed comes back identical.
            int room = content.Length <= region.Length ? region.Length : Align(content.Length);
            places.Add((region, content, position));
            position += room;
        }

        var output = new byte[position];
        file.AsSpan(0, headerSize).CopyTo(output);
        foreach (var (region, content, at) in places)
        {
            content.CopyTo(output.AsSpan(at));
            foreach (int slot in region.Slots)
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(slot * 4), at);
        }
        return output;
    }

    private static int Align(int value) => (value + Alignment - 1) & ~(Alignment - 1);
}
